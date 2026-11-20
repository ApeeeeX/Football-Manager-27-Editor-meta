using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    enum MatchScope { Team, Player, Nation }

    sealed record MatchOption<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    sealed record MatchRating(PlayerAttribute Attribute, string Label, int Value);
    sealed record MatchTarget(string Label, HashSet<int> Ids);

    async Task ShowInMatchAbilityAsync(ulong clubAddress)
    {
        if (backend.Data is not { } game || game.Session.InMatch is not { } editor) return;
        var edits = backend.Edits;
        var window = new Window
        {
            Title = Loc.S("In-Match Ability Modifier (Experimental)"), Width = 820, MinWidth = 780,
            SizeToContent = SizeToContent.Height, MaxHeight = 680, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        using var lifetime = new CancellationTokenSource();
        bool busy = false, writing = false;
        window.Closing += (_, e) => { if (writing) e.Cancel = true; };
        window.Closed += (_, _) => lifetime.Cancel();

        var fixtures = MatchCombo("Select match…");
        var scope = MatchCombo("Select scope…");
        scope.ItemsSource = Enum.GetValues<MatchScope>().Select(s => new MatchOption<MatchScope>(Loc.S(s.ToString()), s)).ToArray();
        var target = new Button { Content = Loc.S("Choose target…"), HorizontalContentAlignment = HorizontalAlignment.Left };
        MatchTarget? chosen = null;
        var position = MatchCombo("All positions");
        position.ItemsSource = new[] { new MatchOption<PlayerPosition?>(Loc.S("All positions"), null) }
            .Concat(Enum.GetValues<PlayerPosition>().Select(p => new MatchOption<PlayerPosition?>(Loc.S(PlayerChangeLabels.Words(p.ToString())), p))).ToArray();
        var familiarity = MatchNumber(1, 20, "18");
        var maxPerformance = new CheckBox { Content = Loc.S("Max performance attributes") };
        var condition = MatchNumber(50, 100, "Optional");
        var fullCondition = new Button { Content = "100%", HorizontalContentAlignment = HorizontalAlignment.Center };
        fullCondition.Click += (_, _) => condition.Value = 100;
        var morale = editor.CanSetMorale ? MatchNumber(1, 20, "Optional") : null;
        var chosenAttribute = MatchCombo("Select an attribute");
        var attributeLabels = PlayerFields.All.Where(f => Enum.TryParse<PlayerAttribute>(f.Id, out var key) && Enum.IsDefined(key))
            .ToDictionary(f => Enum.Parse<PlayerAttribute>(f.Id), f => Loc.Attribute(f.Label));
        chosenAttribute.ItemsSource = attributeLabels.OrderBy(p => p.Value, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new MatchOption<PlayerAttribute>(p.Value, p.Key)).ToArray();
        var rating = MatchNumber(1, 20, "Value");
        rating.Width = 90;
        var add = new Button { Content = Loc.S("Add") };
        var remove = new Button { Content = Loc.S("Remove") };
        var drafts = new Dictionary<PlayerAttribute, MatchRating>();
        var table = ToolGrid.Create();
        table.SelectionMode = DataGridSelectionMode.Single;
        table.Height = 126;
        table.Columns.Add(ToolGrid.Text<MatchRating>("Attribute", row => row.Label, 0, "Attribute", minWidth: 260));
        table.Columns.Add(ToolGrid.Number<MatchRating>("Value", row => row.Value.ToString(), 96, "Value"));
        var scan = new Button { Content = Loc.S("Scan matches") };
        var close = new Button { Content = Loc.S("Close"), IsCancel = true };
        var apply = new Button { Content = Loc.S("Apply to match"), Classes = { "accent" } };
        var notice = new TextBlock { Foreground = UiKit.TextSecondary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var progress = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 3, MinHeight = 3 };
        var targetForm = new UiKit.TwoColumnFormBuilder()
            .AddRow(0, "Match", fixtures).AddRow(0, "Scope", scope)
            .AddRow(1, "Target", target).AddRow(1, "Position", position).AddRow(1, "Minimum familiarity", familiarity)
            .BuildCard("Match and target");
        var changesForm = new UiKit.TwoColumnFormBuilder()
            .AddRow(0, "Preset", maxPerformance).AddRow(1, "Condition (%)", condition, fullCondition).AddRow(1, "Morale", morale)
            .BuildCard("Changes");
        var manual = new StackPanel { Spacing = 8, Margin = new Thickness(12, 10) };
        var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { chosenAttribute, rating, add, remove } };
        manual.Children.Add(entry);
        manual.Children.Add(table);
        var controls = new StackPanel { Spacing = 10, Children = { targetForm, changesForm, UiKit.Card("Attribute overrides", manual) } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { scan, close, apply } };
        DockPanel.SetDock(buttons, Dock.Right);
        var footer = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 12, 0, 0),
            Children = { progress, new DockPanel { LastChildFill = true, Children = { buttons, notice } } },
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        window.Content = new DockPanel
        {
            Margin = new Thickness(16), Children = { footer, new ScrollViewer { Content = controls, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } },
        };

        InMatchFixture? CurrentFixture() => (fixtures.SelectedItem as MatchOption<InMatchFixture>)?.Value;
        PlayerPosition? CurrentPosition() => (position.SelectedItem as MatchOption<PlayerPosition?>)?.Value;

        void Refresh()
        {
            controls.IsEnabled = !busy;
            progress.IsVisible = busy;
            scan.IsEnabled = !busy;
            close.IsEnabled = !writing;
            bool ready = CurrentFixture() is not null;
            target.IsEnabled = ready && scope.SelectedItem is MatchOption<MatchScope>;
            target.Content = chosen?.Label ?? Loc.S("Choose target…");
            familiarity.IsEnabled = CurrentPosition() is not null;
            add.IsEnabled = chosenAttribute.SelectedItem is not null && rating.Value is not null;
            remove.IsEnabled = table.SelectedItem is not null;
            table.IsVisible = drafts.Count > 0;
            apply.IsEnabled = !busy && ready && chosen is not null &&
                (maxPerformance.IsChecked == true || condition.Value.HasValue || morale?.Value is not null || drafts.Count > 0);
        }

        void RefreshRatings()
        {
            table.ItemsSource = drafts.Values.OrderBy(r => r.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
            Refresh();
        }

        bool CurrentSave() => ReferenceEquals(game, backend.Data) && !backend.SaveChanged;

        async Task LoadMatchesAsync()
        {
            if (busy || !CurrentSave()) return;
            busy = true;
            notice.Text = Loc.S("Scanning for active matches…");
            Refresh();
            try
            {
                var result = await Task.Run(() => editor.FindFixtures(game.Players, lifetime.Token), lifetime.Token);
                if (lifetime.IsCancellationRequested || !CurrentSave()) return;
                var available = result.Where(f => f.Teams.Any(team => team.Address == clubAddress)).ToArray();
                fixtures.ItemsSource = available.Select(f => new MatchOption<InMatchFixture>(f.Name, f)).ToArray();
                fixtures.SelectedIndex = -1;
                notice.Text = available.Length == 0 ? Loc.S("No active match found for this club") : "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { notice.Text = UserMessage.For(ex); }
            finally { busy = false; Refresh(); }
        }

        async Task ApplyAsync()
        {
            if (busy || !CurrentSave() || CurrentFixture() is not { } fixture || chosen is not { } selection) return;
            var changes = new InMatchChanges(maxPerformance.IsChecked == true, drafts.ToDictionary(p => p.Key, p => p.Value.Value),
                (int?)condition.Value, (int?)morale?.Value);
            var selectedPosition = CurrentPosition();
            int min = (int?)familiarity.Value ?? 18;
            busy = true;
            notice.Text = Loc.S("Checking the match roster…");
            Refresh();
            try
            {
                var fresh = await Task.Run(() => editor.FindFixtures(game.Players, lifetime.Token), lifetime.Token);
                if (lifetime.IsCancellationRequested || !CurrentSave()) return;
                var current = fresh.SingleOrDefault(fixture.IsSameFixture)
                    ?? throw new InvalidOperationException("The match changed. Scan for matches again.");
                writing = true;
                notice.Text = Loc.S("Applying to match…");
                Refresh();
                var result = await Task.Run(() =>
                {
                    InMatchResult applied = default;
                    // Match addresses are recycled, so they must never become permanent edit-history entries.
                    edits.ExecuteUnrecorded(() => applied = editor.Apply(current, selection.Ids, changes, selectedPosition, min));
                    return applied;
                });
                notice.Text = result.PlayersChanged > 0
                    ? Loc.F("Updated {0} player(s) in this match.", result.PlayersChanged)
                    : Loc.S("No match values needed changing");
                status.Text = notice.Text;
            }
            catch (OperationCanceledException) { }
            catch (StaleObjectException) { notice.Text = Loc.S("The match changed. Pause it and scan again."); }
            catch (Exception ex) { notice.Text = UserMessage.For(ex); }
            finally { writing = busy = false; Refresh(); }
        }

        fixtures.SelectionChanged += (_, _) => { chosen = null; Refresh(); };
        scope.SelectionChanged += (_, _) => { chosen = null; Refresh(); };
        position.SelectionChanged += (_, _) => Refresh();
        target.Click += async (_, _) =>
        {
            if (CurrentFixture() is not { } fixture || scope.SelectedItem is not MatchOption<MatchScope> kind) return;
            var options = MatchTargets(fixture, kind.Value);
            var picked = await PickerDialog.Show<MatchTarget>(window, "Choose target…", query => options
                .Where(item => PickerSearch.Hit(query, item.Label)).Select(item => (item.Label, item)).ToArray(), showAllInitially: true);
            if (picked is not null) chosen = picked;
            Refresh();
        };
        maxPerformance.IsCheckedChanged += (_, _) => Refresh();
        condition.ValueChanged += (_, _) => Refresh();
        if (morale is not null) morale.ValueChanged += (_, _) => Refresh();
        chosenAttribute.SelectionChanged += (_, _) => Refresh();
        rating.ValueChanged += (_, _) => Refresh();
        table.SelectionChanged += (_, _) => Refresh();
        add.Click += (_, _) =>
        {
            if (chosenAttribute.SelectedItem is not MatchOption<PlayerAttribute> field || rating.Value is not { } value) return;
            drafts[field.Value] = new(field.Value, field.Label, (int)value);
            chosenAttribute.SelectedIndex = -1;
            rating.Value = null;
            RefreshRatings();
        };
        remove.Click += (_, _) => { if (table.SelectedItem is MatchRating selected) { drafts.Remove(selected.Attribute); RefreshRatings(); } };
        scan.Click += async (_, _) => await LoadMatchesAsync();
        close.Click += (_, _) => window.Close();
        apply.Click += async (_, _) => await ApplyAsync();
        window.Opened += async (_, _) => await LoadMatchesAsync();
        Refresh();
        await window.ShowDialog(this);
    }

    static IReadOnlyList<MatchTarget> MatchTargets(InMatchFixture fixture, MatchScope scope)
    {
        var members = fixture.Members;
        IEnumerable<MatchTarget> targets = scope switch
        {
            MatchScope.Team => members.GroupBy(m => m.Side.Address).Select(g => new MatchTarget(g.First().Side.DisplayName, Ids(g))),
            MatchScope.Nation => members.Where(m => m.Player.Nationality is not null).GroupBy(m => m.Player.Nationality!.Address)
                .Select(g => new MatchTarget(g.First().Player.NationalityName, Ids(g))),
            _ => members.Select(m => new MatchTarget($"{m.Player.DisplayName} ({m.Side.DisplayName})", [m.Player.Id!.Value])),
        };
        return targets.Where(t => t.Label.Length > 0).OrderBy(t => t.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();

        static HashSet<int> Ids(IEnumerable<InMatchMember> group) => group.Select(m => m.Player.Id!.Value).ToHashSet();
    }

    static ComboBox MatchCombo(string placeholder) => new()
    {
        PlaceholderText = Loc.S(placeholder), Width = 210, MaxDropDownHeight = 260,
    };

    static NumericUpDown MatchNumber(int min, int max, string placeholder) => new()
    {
        Minimum = min, Maximum = max, Watermark = Loc.S(placeholder), ShowButtonSpinner = false,
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };
}
