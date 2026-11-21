using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    sealed record MatchOption<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    static readonly int[] MatchRatingPresets = [20, 16, 12, 1];

    async Task ShowInMatchAbilityAsync(ulong clubAddress)
    {
        if (backend.Data is not { } game || game.Session.InMatch is not { } editor) return;
        var edits = backend.Edits;
        var window = new Window
        {
            Title = Loc.S("In-Match Ability Modifier (Experimental)"),
            Width = 1150, Height = 700, MinWidth = 955, MinHeight = 490,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        using var lifetime = new CancellationTokenSource();
        var cancellation = lifetime.Token;
        bool busy = false, writing = false, filling = false;
        window.Closing += (_, e) => { if (writing) e.Cancel = true; };
        window.Closed += (_, _) => lifetime.Cancel();

        var fixtures = MatchCombo("Select match…", 290);
        var scan = MatchButton("Scan matches");
        var teamButtons = new[] { new Button(), new Button() };
        var both = MatchButton("Both teams");
        var clearSelection = MatchButton("Clear selection");
        var search = new TextBox { Watermark = Loc.S("Find a match player…"), MinWidth = 0 };
        var nation = MatchCombo("Filter by nation", 145);
        var position = MatchCombo("All positions", 172);
        position.ItemsSource = new[] { new MatchOption<PlayerPosition?>(Loc.S("All positions"), null) }
            .Concat(Enum.GetValues<PlayerPosition>().Select(p => new MatchOption<PlayerPosition?>(PositionLabels.Spaced(p), p))).ToArray();
        var familiarity = MatchNumber(1, 20, "18");
        familiarity.Width = 48;
        ToolTip.SetTip(familiarity, Loc.S("Minimum familiarity"));
        Avalonia.Automation.AutomationProperties.SetName(familiarity, Loc.S("Minimum familiarity"));
        var roster = ToolGrid.Create();
        var selectedCount = new TextBlock { Foreground = UiKit.TextSecondary, TextWrapping = TextWrapping.Wrap };
        var excluded = new HashSet<ulong>();
        InMatchMember[] members = [], visibleMembers = [];
        IReadOnlyDictionary<int, InMatchPlayerValues> values = new Dictionary<int, InMatchPlayerValues>();

        var heal = MatchButton("Heal");
        var inspire = MatchButton("Inspire");
        var allInOne = MatchButton("All in One");
        var destroy = MatchButton("Destroy");
        var quickActions = new[] { heal, inspire, allInOne, destroy };
        ToolTip.SetTip(heal, Loc.S("Restore match condition to 100%."));
        ToolTip.SetTip(inspire, Loc.S("Set match morale to 20."));
        ToolTip.SetTip(allInOne, Loc.S("Restore match condition and morale, and raise performance attributes."));
        ToolTip.SetTip(destroy, Loc.S("Set match condition to 50%, morale to 1 and performance attributes to 1."));
        var availableAttributes = editor.Attributes.ToHashSet();
        var ratings = PlayerFields.All
            .Where(field => Enum.TryParse<PlayerAttribute>(field.Id, out var attribute) && availableAttributes.Contains(attribute))
            .ToDictionary(field => Enum.Parse<PlayerAttribute>(field.Id), field => (
                Label: Loc.Attribute(field.Label), Box: CreateRatingInput()));
        foreach (var (label, box) in ratings.Values)
        {
            Avalonia.Automation.AutomationProperties.SetName(box, label);
            box.MaxLength = 2;
        }

        var setRatings = MatchRatingPresets.Select(value => new Button { Content = value.ToString(Inv), MinWidth = 36 }).ToArray();
        var clearChanges = MatchButton("Clear changes");
        var condition = MatchNumber(50, 100, "Optional");
        condition.Width = 70;
        var morale = MatchNumber(1, 20, "Optional");
        morale.Width = 70;

        Control Group(AttributeGroup group) => AttributeCard(group.ToString(), ratings
            .Where(pair => PlayerAttributeInfo.GroupOf(pair.Key) == group)
            .Select(pair => AttributeRow(pair.Value.Label, pair.Value.Box)));
        var attributeCards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 8 };
        var technical = Group(AttributeGroup.Technical);
        var mental = Group(AttributeGroup.Mental);
        technical.VerticalAlignment = mental.VerticalAlignment = VerticalAlignment.Top;
        var physical = new StackPanel
        {
            Spacing = 8, Children = { Group(AttributeGroup.Physical), Group(AttributeGroup.Goalkeeping), Group(AttributeGroup.Hidden) },
        };
        Grid.SetColumn(mental, 1);
        Grid.SetColumn(physical, 2);
        attributeCards.Children.Add(technical);
        attributeCards.Children.Add(mental);
        attributeCards.Children.Add(physical);

        var tools = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                UiKit.Card("Match presets", new Border { Padding = new Thickness(8), Child = MatchBar(quickActions) }),
                new UiKit.TwoColumnFormBuilder()
                    .AddRow(0, "Condition (%)", condition)
                    .AddRow(1, "Morale", editor.CanSetMorale ? morale : null)
                    .BuildCard("Condition and morale"),
                MatchBar(new Control[] { new TextBlock { Text = Loc.S("Set all attributes"), VerticalAlignment = VerticalAlignment.Center } }
                    .Concat(setRatings).Append(clearChanges)),
                attributeCards,
            },
        };
        inspire.IsVisible = editor.CanSetMorale;

        var rosterHeader = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                MatchBar(teamButtons.Cast<Control>().Append(both).Append(clearSelection)),
                search, MatchBar([nation, position, familiarity]), selectedCount,
            },
        };
        DockPanel.SetDock(rosterHeader, Dock.Top);
        var rosterPanel = UiKit.Card("Match players", new DockPanel
        {
            Margin = new Thickness(8), Children = { rosterHeader, roster },
        }, fill: true);
        var editorPanel = new ScrollViewer { Content = tools, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 10 };
        Grid.SetColumn(editorPanel, 1);
        columns.Children.Add(rosterPanel);
        columns.Children.Add(editorPanel);

        var close = MatchButton("Close");
        close.IsCancel = true;
        var apply = MatchButton("Apply to match");
        apply.Classes.Add("accent");
        var buttons = MatchBar([close, apply]);
        DockPanel.SetDock(buttons, Dock.Right);
        var notice = new TextBlock { Foreground = UiKit.TextSecondary, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var progress = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 3, MinHeight = 3 };
        var footer = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 10, 0, 0),
            Children = { progress, new DockPanel { LastChildFill = true, Children = { buttons, notice } } },
        };
        var header = MatchBar([fixtures, scan]);
        header.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        window.Content = new DockPanel { Margin = new Thickness(12), Children = { header, footer, columns } };

        var fixtureValues = new Dictionary<InMatchFixture, IReadOnlyDictionary<int, InMatchPlayerValues>>();
        Action syncTicks = ToolGrid.AddTick<InMatchMember>(roster, excluded, MemberKey, () => members, SelectionChanged);
        roster.Columns.Add(ToolGrid.Text<InMatchMember>("Player", row => row.Player.DisplayName, 0, "Player", minWidth: 94));
        roster.Columns.Add(ToolGrid.Text<InMatchMember>("Team", row => row.Side.DisplayName, 73, "Team"));
        roster.Columns.Add(ToolGrid.Number<InMatchMember>("Condition", row =>
            ReadCondition(row) is { } percent ? $"{percent}%" : "", 67, "Condition (%)"));
        ToolGrid.EnableSort(roster, new Dictionary<string, Func<InMatchMember, IComparable?>>
        {
            ["Player"] = row => row.Player.DisplayName,
            ["Team"] = row => row.Side.DisplayName,
            ["Condition"] = row => ReadCondition(row),
        });

        int? ReadCondition(InMatchMember row) => values.GetValueOrDefault(row.Player.Id!.Value)?.ConditionPercent;
        InMatchFixture? CurrentFixture() => (fixtures.SelectedItem as MatchOption<InMatchFixture>)?.Value;
        bool CurrentSave() => ReferenceEquals(game, backend.Data) && !backend.SaveChanged;
        InMatchMember[] Selected() => members.Where(member => !excluded.Contains(MemberKey(member))).ToArray();

        void Refresh()
        {
            if (filling) return;
            bool ready = !busy && CurrentSave() && CurrentFixture() is not null;
            int count = Selected().Length;
            columns.IsEnabled = ready;
            progress.IsVisible = busy;
            scan.IsEnabled = !busy;
            fixtures.IsEnabled = !busy;
            close.IsEnabled = !writing;
            foreach (var button in quickActions) button.IsEnabled = count > 0;
            clearSelection.IsEnabled = count > 0;
            familiarity.IsEnabled = position.SelectedItem is MatchOption<PlayerPosition?> { Value: not null };
            bool hasDraft = ratings.Values.Any(r => !string.IsNullOrWhiteSpace(r.Box.Text))
                || condition.Value.HasValue || morale.Value.HasValue;
            clearChanges.IsEnabled = hasDraft;
            apply.IsEnabled = ready && count > 0 && hasDraft && ratings.Values.All(r => MatchRatingIsValid(r.Box.Text));
        }

        void SelectionChanged()
        {
            var selected = Selected();
            int shown = visibleMembers.Count(member => !excluded.Contains(MemberKey(member)));
            selectedCount.Text = shown == selected.Length
                ? Loc.F("{0} players selected", selected.Length)
                : Loc.F("{0} players selected ({1} outside filters)", selected.Length, selected.Length - shown);
            var snapshots = selected.Select(member => values.GetValueOrDefault(member.Player.Id!.Value)).ToArray();
            foreach (var (attribute, row) in ratings)
                row.Box.Watermark = CommonValue(sample => sample.Attributes.GetValueOrDefault(attribute));
            condition.Watermark = CommonValue(sample => sample.ConditionPercent) ?? Loc.S("Optional");
            morale.Watermark = CommonValue(sample => sample.Morale) ?? Loc.S("Optional");
            Refresh();

            string? CommonValue(Func<InMatchPlayerValues, int?> get)
            {
                if (snapshots.Length == 0 || snapshots[0] is not { } first || get(first) is not { } value) return null;
                return snapshots.All(sample => sample is not null && get(sample) == value) ? value.ToString(Inv) : null;
            }
        }

        void FilterRoster()
        {
            ulong? country = (nation.SelectedItem as MatchOption<ulong?>)?.Value;
            PlayerPosition? role = (position.SelectedItem as MatchOption<PlayerPosition?>)?.Value;
            int minimum = (int?)familiarity.Value ?? 18;
            visibleMembers = members.Where(member =>
                PickerSearch.Hit(search.Text?.Trim() ?? "", member.Player.DisplayName) &&
                (country is null || member.Player.Nationality?.Address == country) &&
                (role is null || member.Player.Position(role.Value) >= minimum)).ToArray();
            ToolGrid.SetItems(roster, visibleMembers);
            SelectionChanged();
        }

        void SelectTeams(IReadOnlySet<ulong> sides)
        {
            excluded.Clear();
            foreach (var member in members)
                if (!sides.Contains(member.Side.Address)) excluded.Add(MemberKey(member));
            syncTicks();
        }

        void FillRatings(int? value)
        {
            filling = true;
            string? text = value?.ToString(Inv);
            foreach (var row in ratings.Values) row.Box.Text = text;
            filling = false;
            Refresh();
        }

        async Task LoadMatchesAsync()
        {
            if (busy || !CurrentSave()) return;
            busy = true;
            var previous = CurrentFixture();
            var previousSelection = Selected().Select(MemberKey).ToHashSet();
            notice.Text = Loc.S("Scanning for active matches…");
            Refresh();
            try
            {
                var result = await Task.Run(() =>
                {
                    var available = editor.FindFixtures(game.Players, cancellation)
                        .Where(fixture => fixture.Teams.Any(team => team.Address == clubAddress)).ToArray();
                    var snapshots = available.ToDictionary(fixture => fixture, fixture => editor.ReadPlayers(fixture, cancellation));
                    return (available, snapshots);
                }, cancellation);
                if (cancellation.IsCancellationRequested || !CurrentSave()) return;
                fixtureValues = result.snapshots;
                fixtures.ItemsSource = result.available.Select(f => new MatchOption<InMatchFixture>(f.Name, f)).ToArray();
                fixtures.SelectedIndex = previous is null ? -1 : Array.FindIndex(result.available, previous.IsSameFixture);
                if (fixtures.SelectedIndex >= 0)
                {
                    excluded.Clear();
                    foreach (var member in members)
                        if (!previousSelection.Contains(MemberKey(member))) excluded.Add(MemberKey(member));
                    syncTicks();
                }
                if (fixtures.SelectedIndex < 0 && result.available.Length == 1) fixtures.SelectedIndex = 0;
                notice.Text = result.available.Length == 0 ? Loc.S("No active match found for this club") : "";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { notice.Text = UserMessage.For(ex); }
            finally { busy = false; Refresh(); }
        }

        async Task ApplyAsync(InMatchChanges changes, bool clearDraft = false)
        {
            if (busy || !CurrentSave() || CurrentFixture() is not { } fixture) return;
            var ids = Selected().Select(member => member.Player.Id!.Value).ToHashSet();
            if (ids.Count == 0) return;
            busy = true;
            notice.Text = Loc.S("Checking the match roster…");
            Refresh();
            try
            {
                var fresh = await Task.Run(() => editor.FindFixtures(game.Players, cancellation), cancellation);
                if (cancellation.IsCancellationRequested || !CurrentSave()) return;
                var current = fresh.SingleOrDefault(fixture.IsSameFixture)
                    ?? throw new StaleObjectException("The match changed. Scan for matches again.");
                writing = true;
                notice.Text = Loc.S("Applying to match…");
                Refresh();
                var result = await Task.Run(() =>
                {
                    InMatchResult applied = default;
                    // Match copies are recycled and cannot be the targets of persistent undo entries.
                    edits.ExecuteUnrecorded(() =>
                    {
                        if (!CurrentSave()) throw new StaleObjectException("The save changed during match editing.");
                        applied = editor.Apply(current, ids, changes);
                    });
                    return (applied, snapshots: editor.ReadPlayers(current));
                });
                values = result.snapshots;
                fixtureValues[fixture] = values;
                ToolGrid.SetItems(roster, visibleMembers);
                syncTicks();
                if (clearDraft) ResetDraft();
                notice.Text = result.applied.PlayersChanged > 0
                    ? Loc.F("Updated {0} player(s) in this match.", result.applied.PlayersChanged)
                    : Loc.S("No match values needed changing");
                status.Text = notice.Text;
            }
            catch (OperationCanceledException) { }
            catch (StaleObjectException) { notice.Text = Loc.S("The match changed. Pause it and scan again."); }
            catch (Exception ex) { notice.Text = UserMessage.For(ex); }
            finally { writing = busy = false; Refresh(); }
        }

        void ResetDraft()
        {
            condition.Value = null;
            morale.Value = null;
            FillRatings(null);
        }

        fixtures.SelectionChanged += (_, _) =>
        {
            var fixture = CurrentFixture();
            members = fixture?.Members.OrderBy(member => member.Side.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(member => member.Player.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray() ?? [];
            values = fixture is not null && fixtureValues.TryGetValue(fixture, out var snapshots)
                ? snapshots : new Dictionary<int, InMatchPlayerValues>();
            excluded.Clear();
            foreach (var member in members) excluded.Add(MemberKey(member));
            for (int i = 0; i < teamButtons.Length; i++)
            {
                var team = fixture?.Teams[i];
                teamButtons[i].Content = team?.DisplayName;
                teamButtons[i].Tag = team?.Address;
                teamButtons[i].IsVisible = team is not null;
            }
            var countries = members.Select(member => member.Player.Nationality).OfType<NationInfo>()
                .DistinctBy(country => country.Address).OrderBy(country => country.Name, StringComparer.CurrentCultureIgnoreCase);
            nation.ItemsSource = new[] { new MatchOption<ulong?>(Loc.S("All nations"), null) }
                .Concat(countries.Select(country => new MatchOption<ulong?>(country.Name ?? "", country.Address))).ToArray();
            nation.SelectedIndex = -1;
            FilterRoster();
            syncTicks();
        };
        foreach (var team in teamButtons)
            team.Click += (_, _) => { if (team.Tag is ulong side) SelectTeams(new HashSet<ulong> { side }); };
        both.Click += (_, _) => SelectTeams(CurrentFixture()?.Teams.Select(team => team.Address).ToHashSet() ?? []);
        clearSelection.Click += (_, _) => SelectTeams(new HashSet<ulong>());
        search.TextChanged += (_, _) => FilterRoster();
        nation.SelectionChanged += (_, _) => FilterRoster();
        position.SelectionChanged += (_, _) => FilterRoster();
        familiarity.ValueChanged += (_, _) => FilterRoster();
        condition.ValueChanged += (_, _) => Refresh();
        morale.ValueChanged += (_, _) => Refresh();
        for (int i = 0; i < setRatings.Length; i++)
        {
            int value = MatchRatingPresets[i];
            Avalonia.Automation.AutomationProperties.SetName(setRatings[i], Loc.F("Set all attributes to {0}", value));
            setRatings[i].Click += (_, _) => FillRatings(value);
        }
        foreach (var row in ratings.Values)
            row.Box.TextChanged += (_, _) =>
            {
                int rating = MatchRating(row.Box.Text);
                row.Box.Foreground = rating > 0 ? RatingColor(rating) : UiKit.TextSecondary;
                row.Box.Background = rating > 0 ? RatingBg(rating) : null;
                row.Box.BorderBrush = MatchRatingIsValid(row.Box.Text)
                    ? rating > 0 ? RatingBorder(rating) : null
                    : UiKit.Brush("#F87171");
                Refresh();
            };

        var none = new Dictionary<PlayerAttribute, int>();
        var weaken = availableAttributes.Where(attribute => PlayerAttributeInfo.GroupOf(attribute) != AttributeGroup.Hidden)
            .ToDictionary(attribute => attribute, _ => 1);
        heal.Click += async (_, _) => await ApplyAsync(new(false, none, 100));
        inspire.Click += async (_, _) => await ApplyAsync(new(false, none, null, 20));
        allInOne.Click += async (_, _) => await ApplyAsync(new(true, none, 100, editor.CanSetMorale ? 20 : null));
        destroy.Click += async (_, _) => await ApplyAsync(new(false, weaken, 50, editor.CanSetMorale ? 1 : null));
        clearChanges.Click += (_, _) => ResetDraft();
        apply.Click += async (_, _) =>
        {
            if (!ratings.Values.All(row => MatchRatingIsValid(row.Box.Text))) return;
            var changes = ratings.Where(pair => MatchRating(pair.Value.Box.Text) > 0)
                .ToDictionary(pair => pair.Key, pair => MatchRating(pair.Value.Box.Text));
            await ApplyAsync(new(false, changes, (int?)condition.Value, editor.CanSetMorale ? (int?)morale.Value : null), clearDraft: true);
        };

        scan.Click += async (_, _) => await LoadMatchesAsync();
        close.Click += (_, _) => window.Close();
        window.Opened += async (_, _) => await LoadMatchesAsync();
        syncTicks();
        Refresh();
        await window.ShowDialog(this);
    }

    static ulong MemberKey(InMatchMember member) => (ulong)member.Player.Id!.Value;
    static bool MatchRatingIsValid(string? text) => string.IsNullOrWhiteSpace(text) || MatchRating(text) > 0;
    static int MatchRating(string? text) => int.TryParse(text, out int value) && value is >= 1 and <= 20 ? value : 0;

    static Button MatchButton(string label) => new() { Content = Loc.S(label) };

    static WrapPanel MatchBar(IEnumerable<Control> children)
    {
        var panel = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    static ComboBox MatchCombo(string placeholder, double width) => new()
    {
        PlaceholderText = Loc.S(placeholder), Width = width, MaxDropDownHeight = 260,
    };

    static NumericUpDown MatchNumber(int min, int max, string placeholder) => new()
    {
        Minimum = min, Maximum = max, Watermark = Loc.S(placeholder), ShowButtonSpinner = false,
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };
}
