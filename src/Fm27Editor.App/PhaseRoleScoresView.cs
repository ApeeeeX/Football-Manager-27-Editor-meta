using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record PhaseRoleRow(PhaseRole Role, string Name, double? Score);
    const string BundledCatalog = "Fm27Editor.Ratings.rolesAndDuties.json";
    static string? phaseRoleCatalogPath;

    /// <summary>The role catalog embedded in the exe; null only for a build made without it.</summary>
    internal static PhaseRoleRatings? LoadBundledRoleCatalog()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(BundledCatalog);
        return stream is null ? null : PhaseRoleRatings.Load(stream);
    }

    internal static string? FindPhaseRoleCatalog()
    {
        if (phaseRoleCatalogPath is { } chosen && File.Exists(chosen)) return chosen;
        // The catalog is embedded; these files remain a fallback for a build without the resource, and let a player
        // point at a newer FMRTE catalog with Choose role catalog.
        string[] candidates =
        [
            "/Applications/FMRTE 27.app/Contents/MacOS/Assets/Ratings/rolesAndDuties.json",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FMRTE 27", "Assets", "Ratings", "rolesAndDuties.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FMRTE 27", "Assets", "Ratings", "rolesAndDuties.json"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    Control RoleScoresPanel(PlayerDraft draft)
    {
        var root = new StackPanel { Spacing = 8 };
        var source = new TextBlock { Foreground = UiKit.TextMuted, TextWrapping = TextWrapping.Wrap };
        var content = new ContentControl();
        var import = new Button { Content = Loc.S("Choose role catalog…") };
        var position = new ComboBox
        {
            ItemsSource = Enum.GetValues<PlayerPosition>().Select(p => new PhasePosition(p)).ToArray(),
            SelectedIndex = 0, MinWidth = 100,
        };
        var (_, positions) = draft.PreviewRatings();
        position.SelectedItem = position.Items.OfType<PhasePosition>().OrderByDescending(p => positions[(int)p.Position]).First();
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, Children = { position, import } };
        root.Children.Add(bar); root.Children.Add(source); root.Children.Add(content);
        PhaseRoleRatings? catalog = null;
        PhaseRoleRatings? displayedCatalog = null;
        byte[]? displayedAttributes = null;
        PlayerPosition displayedPosition = default;
        int displayedFamiliarity = 0;
        void Render()
        {
            if (catalog is null || position.SelectedItem is not PhasePosition selectedPosition) return;
            var (raw, currentPositions) = draft.PreviewRatings();
            int familiarity = currentPositions[(int)selectedPosition.Position];
            if (ReferenceEquals(displayedCatalog, catalog) && displayedPosition == selectedPosition.Position &&
                displayedFamiliarity == familiarity && raw.AsSpan().SequenceEqual(displayedAttributes))
                return;
            content.Content = BuildPhaseRoleTables(catalog, selectedPosition.Position, raw, draft.Original.AttributeScale, familiarity);
            displayedCatalog = catalog;
            displayedPosition = selectedPosition.Position;
            displayedFamiliarity = familiarity;
            displayedAttributes = raw;
        }
        void Load(string? path)
        {
            try
            {
                catalog = path is null ? null : PhaseRoleRatings.Load(path);
                source.Text = path is null ? Loc.S("Choose an IP/OOP role catalog to show ratings.") : Loc.F("Role catalog: {0}", path);
                ToolTip.SetTip(source, source.Text);
                Render();
            }
            catch (Exception ex) { catalog = null; content.Content = null; source.Text = UserMessage.For(ex); }
        }
        position.SelectionChanged += (_, _) => Render();
        root.AttachedToVisualTree += (_, _) => Render();
        import.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.S("Choose role catalog…"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            Load(path);
            if (catalog is not null) phaseRoleCatalogPath = path;
        };
        LoadBundled();
        return root;

        void LoadBundled()
        {
            try
            {
                catalog = LoadBundledRoleCatalog();
                if (catalog is null) { Load(FindPhaseRoleCatalog()); return; }
                import.IsVisible = false;
                source.IsVisible = false;
                Render();
            }
            catch (Exception ex) { catalog = null; content.Content = null; source.Text = UserMessage.For(ex); }
        }
    }

    sealed record PhasePosition(PlayerPosition Position)
    { public override string ToString() => PositionLabels.Short(Position); }

    internal static Control BuildPhaseRoleTables(PhaseRoleRatings catalog, PlayerPosition position, byte[] attributes, int scale, int familiarity)
    {
        Control Table(PossessionPhase phase)
        {
            var grid = ToolGrid.Create();
            grid.Height = 300;
            grid.VerticalAlignment = VerticalAlignment.Top;
            grid.Columns.Add(ToolGrid.StarText<PhaseRoleRow>("Role", r => r.Name, 1, 105, "Role"));
            grid.Columns.Add(ToolGrid.Number<PhaseRoleRow>("Score", r => r.Score is { } v ? v.ToString("0.0", Loc.FormatCulture) + "%" : "—", 72, "Score"));
            var rows = catalog.At(position, phase).Select(role => new PhaseRoleRow(
                role, Loc.S(ContractLabels.Spaced(role.Id)), catalog.Score(attributes, scale, position, familiarity, role)))
                .OrderByDescending(r => r.Score).ToArray();
            if (rows.Length == 0)
                return new TextBlock { Text = Loc.S("No roles for this position and phase."), TextWrapping = TextWrapping.Wrap };

            grid.SelectionMode = DataGridSelectionMode.Single;
            ToolGrid.SetItems(grid, rows);
            ToolGrid.EnableSort<PhaseRoleRow>(grid, new Dictionary<string, Func<PhaseRoleRow, IComparable?>> { ["Role"] = r => r.Name, ["Score"] = r => r.Score });
            grid.SelectedItem = rows[0];
            var detail = new ContentControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = BuildRoleAttributes(rows[0].Role, attributes, scale),
            };
            grid.SelectionChanged += (_, _) =>
            {
                if (grid.SelectedItem is PhaseRoleRow selectedRole)
                    detail.Content = BuildRoleAttributes(selectedRole.Role, attributes, scale);
            };
            var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10, Children = { grid, detail } };
            Grid.SetColumn(detail, 1);
            return layout;
        }
        return UiKit.OptionTabs([("In Possession", Table(PossessionPhase.InPossession)), ("Out of Possession", Table(PossessionPhase.OutOfPossession))]);
    }

    static Control BuildRoleAttributes(PhaseRole role, byte[] attributes, int scale)
    {
        Control AttributeValue(PhaseRoleAttribute entry)
        {
            var label = new TextBlock
            {
                Text = ScoutingLabels.Attribute(entry.Attribute), TextWrapping = TextWrapping.Wrap,
                Foreground = UiKit.TextSecondary, VerticalAlignment = VerticalAlignment.Center,
            };
            byte raw = (int)entry.Attribute < attributes.Length ? attributes[(int)entry.Attribute] : (byte)0;
            int? value = raw != 0 && scale > 0 ? AttributeScaling.ToDisplay(raw, scale) : null;
            var rating = new TextBlock
            {
                Text = value?.ToString(Loc.FormatCulture) ?? "—", FontWeight = FontWeight.SemiBold,
                Foreground = value is { } score ? RatingColor(score) : UiKit.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            return UiKit.ActionRow(label, rating);
        }

        var detail = new StackPanel { Spacing = 8 };
        var key = role.Attributes.Where(a => a.Key && !a.LowerIsBetter).ToArray();
        var preferable = role.Attributes.Where(a => !a.Key && !a.LowerIsBetter).ToArray();
        var lower = role.Attributes.Where(a => a.LowerIsBetter).ToArray();
        if (key.Length > 0)
            detail.Children.Add(AttributeCard("Key for role", key.Select(AttributeValue)));
        if (preferable.Length + lower.Length > 0)
        {
            var secondary = new StackPanel { Spacing = 8 };
            if (preferable.Length > 0)
                secondary.Children.Add(AttributeCard("Preferable", preferable.Select(AttributeValue)));
            if (lower.Length > 0)
                secondary.Children.Add(AttributeCard("Lower is better", lower.Select(AttributeValue)));
            detail.Children.Add(new Expander { Header = Loc.S("Other role attributes"), Content = secondary, IsExpanded = key.Length == 0 });
        }
        return detail;
    }
}
