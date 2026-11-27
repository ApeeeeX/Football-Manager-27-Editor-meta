using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Players;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    MyClubCompositionView myClubView = null!;
    internal LoadedGameData? MyClubLoadedData => backend.Data;
    internal GameSession MyClubSession => backend.Session;
    internal GraphicsCatalog MyClubGraphics => graphics;
    internal void ShowMyClubMessage(string message) => status.Text = message;
}

/// <summary>A local team sheet: it reads a club squad, but its positions and presets never write game memory.</summary>
internal sealed class MyClubCompositionView
{
    static readonly DataFormat<string> PlayerDragFormat = DataFormat.CreateStringApplicationFormat("Fm27Editor.MyClub.Player");
    const double PitchWidth = 400, PitchHeight = 620;
    const double MarkerWidth = 70, MarkerHeight = 80;
    const int MaxPitchPlayers = 11;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly MyClubPitchSlot[] PitchSlots =
    [
        new(PlayerPosition.Striker, .31, .13), new(PlayerPosition.Striker, .50, .13), new(PlayerPosition.Striker, .69, .13),
        new(PlayerPosition.AttackingMidLeft, .12, .29), new(PlayerPosition.AttackingMidCentral, .31, .29), new(PlayerPosition.AttackingMidCentral, .50, .29), new(PlayerPosition.AttackingMidCentral, .69, .29), new(PlayerPosition.AttackingMidRight, .88, .29),
        new(PlayerPosition.MidfielderLeft, .12, .45), new(PlayerPosition.MidfielderCentral, .31, .45), new(PlayerPosition.MidfielderCentral, .50, .45), new(PlayerPosition.MidfielderCentral, .69, .45), new(PlayerPosition.MidfielderRight, .88, .45),
        new(PlayerPosition.WingBackLeft, .12, .61), new(PlayerPosition.DefensiveMidfielder, .31, .61), new(PlayerPosition.DefensiveMidfielder, .50, .61), new(PlayerPosition.DefensiveMidfielder, .69, .61), new(PlayerPosition.WingBackRight, .88, .61),
        new(PlayerPosition.DefenderLeft, .12, .77), new(PlayerPosition.DefenderCentral, .31, .77), new(PlayerPosition.DefenderCentral, .50, .77), new(PlayerPosition.DefenderCentral, .69, .77), new(PlayerPosition.DefenderRight, .88, .77),
        new(PlayerPosition.Goalkeeper, .50, .93),
    ];
    readonly MainWindow window;
    readonly ComboBox clubPicker = new() { MinWidth = 220, MaxDropDownHeight = 500 };
    readonly ComboBox teamPicker = new() { MinWidth = 150, MaxDropDownHeight = 400 };
    readonly ComboBox presetPicker = new() { MinWidth = 170, MaxDropDownHeight = 400 };
    readonly TextBox presetName = new() { Watermark = "Preset name…", Width = 150 };
    readonly TextBox search = new() { Watermark = "Search players…", Width = 220 };
    readonly DataGrid roster = ToolGrid.Create();
    readonly Canvas pitch = PitchSurface.Create(PitchWidth, PitchHeight);
    readonly List<Control> pitchMarkers = [];
    readonly List<Control> positionSpots = [];
    readonly TextBlock rosterCount = new() { FontSize = 12, Foreground = UiKit.TextMuted, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock emptyState = new() { FontSize = 14, Foreground = UiKit.TextMuted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24) };
    readonly Border pitchHost;
    readonly Grid contentGrid = new();
    readonly List<MyClubRosterRow> allRows = [];
    readonly Dictionary<ulong, PitchAssignment> assignments = [];
    readonly Dictionary<string, List<MyClubPresetPlayer>> sessionDrafts = new(StringComparer.Ordinal);
    IReadOnlyList<ClubEntry> clubs = [];
    IReadOnlyList<PlayerRecord> teamPlayers = [];
    LoadedGameData? loadedData;
    ClubEntry? selectedClub;
    TeamEntry? selectedTeam;
    PlayerRecord? selectedPlayer;
    Control? draggedMarker;
    Control? dragPreview;
    bool draggedMarkerMoved;
    MyClubRosterRow? pressedRosterRow;
    Point? pressPoint;
    bool updatingPickers;
    bool showPositionTargets;
    string? selectedPresetName;

    internal Control Root { get; }

    sealed record ClubChoice(ClubEntry Club)
    {
        public override string ToString() => Club.DisplayName;
    }
    sealed record TeamChoice(TeamEntry Team, string Name)
    {
        public override string ToString() => Name;
    }
    sealed record PresetChoice(string Name)
    {
        public override string ToString() => Name;
    }
    sealed record MyClubRosterRow(PlayerRecord Player)
    {
        public string Name => Player.DisplayName;
        public int? Age => Player.Age;
        public string DisplayAge => Age?.ToString(Inv) ?? "—";
        public int Ca => Player.CurrentAbility;
        public int? Mca => McaCalculator.IsGoalkeeper(Player) ? null : ScoutingMetrics.McaRounded(Player);
        public string DisplayMca => Mca?.ToString(Inv) ?? "—";
        public int Pa => Player.PotentialAbility;
        public string Positions => string.Join(", ", Enum.GetValues<PlayerPosition>()
            .Where(p => Player.Position(p) > 10)
            .OrderByDescending(p => Player.Position(p))
            .Select(PositionLabels.Short));
    }
    sealed class PitchAssignment(PlayerRecord player, PlayerPosition position, double x, double y)
    {
        public PlayerRecord Player { get; set; } = player;
        public PlayerPosition Position { get; set; } = position;
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public Control? Marker { get; set; }
    }
    sealed record MyClubPitchSlot(PlayerPosition Position, double X, double Y);

    public MyClubCompositionView(MainWindow mainWindow)
    {
        window = mainWindow;
        roster.SelectionMode = DataGridSelectionMode.Single;
        roster.RowHeight = 30;
        pitchHost = new Border
        {
            BorderBrush = UiKit.BorderSubtle, BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = pitch },
        };
        Root = BuildRoot();
        ConfigureRoster();
        WireEvents();
        emptyState.Text = Loc.S("Load Game to view a club squad.");
        Grid.SetColumnSpan(emptyState, 2);
        contentGrid.Children.Add(emptyState);
    }

    Control BuildRoot()
    {
        var clubLabel = new TextBlock { Text = Loc.S("Club"), Foreground = UiKit.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        var teamLabel = new TextBlock { Text = Loc.S("Team"), Foreground = UiKit.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        var presetLabel = new TextBlock { Text = Loc.S("Preset"), Foreground = UiKit.TextMuted, VerticalAlignment = VerticalAlignment.Center };
        var savePreset = new Button { Content = Loc.S("Save preset") };
        var deletePreset = new Button { Content = Loc.S("Delete preset") };
        savePreset.Click += (_, _) => SavePreset();
        deletePreset.Click += (_, _) => DeletePreset();
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 5, Margin = new Thickness(12, 8) };
        tools.Children.Add(clubLabel); tools.Children.Add(clubPicker);
        tools.Children.Add(teamLabel); tools.Children.Add(teamPicker);
        tools.Children.Add(presetLabel); tools.Children.Add(presetPicker);
        tools.Children.Add(presetName); tools.Children.Add(savePreset); tools.Children.Add(deletePreset);

        var pitchTitle = new TextBlock { Text = Loc.S("Team composition"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = UiKit.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        var remove = new Button { Content = Loc.S("Remove from pitch"), HorizontalAlignment = HorizontalAlignment.Left };
        remove.Click += (_, _) => RemoveSelected();
        var pitchHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 2, 0, 8) };
        pitchHeader.Children.Add(pitchTitle);
        Grid.SetColumn(remove, 1);
        pitchHeader.Children.Add(remove);
        var leftPanel = new DockPanel { Margin = new Thickness(12, 0, 8, 12), LastChildFill = true };
        DockPanel.SetDock(pitchHeader, Dock.Top);
        leftPanel.Children.Add(pitchHeader);
        leftPanel.Children.Add(pitchHost);

        var listHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8, Margin = new Thickness(8, 0, 12, 8) };
        listHeader.Children.Add(new TextBlock { Text = Loc.S("Squad"), FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = UiKit.TextPrimary, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(search, 1);
        Grid.SetColumn(rosterCount, 2);
        listHeader.Children.Add(search);
        listHeader.Children.Add(rosterCount);
        var rightPanel = new DockPanel { Margin = new Thickness(4, 0, 0, 12), LastChildFill = true };
        DockPanel.SetDock(listHeader, Dock.Top);
        rightPanel.Children.Add(listHeader);
        rightPanel.Children.Add(roster);

        contentGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
        contentGrid.RowDefinitions = new RowDefinitions("*");
        Grid.SetColumn(leftPanel, 0); contentGrid.Children.Add(leftPanel);
        Grid.SetColumn(rightPanel, 1); contentGrid.Children.Add(rightPanel);
        var page = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(tools, Dock.Top);
        page.Children.Add(tools);
        page.Children.Add(contentGrid);
        return page;
    }

    void ConfigureRoster()
    {
        roster.Columns.Add(new DataGridTemplateColumn
        {
            Header = ToolGrid.Header("", "Player photo"),
            Width = new DataGridLength(42), MinWidth = 42, CanUserResize = false,
            CellTemplate = new FuncDataTemplate<MyClubRosterRow>((row, _) => row is null ? null : BuildPortrait(row.Player, 34)),
        });
        roster.Columns.Add(Column("Name", nameof(MyClubRosterRow.Name), new DataGridLength(1, DataGridLengthUnitType.Star), 125));
        Numeric("Age", nameof(MyClubRosterRow.DisplayAge), nameof(MyClubRosterRow.Age), 48);
        Numeric("CA", nameof(MyClubRosterRow.Ca), nameof(MyClubRosterRow.Ca), 48);
        Numeric("MCA", nameof(MyClubRosterRow.DisplayMca), nameof(MyClubRosterRow.Mca), 52);
        Numeric("PA", nameof(MyClubRosterRow.Pa), nameof(MyClubRosterRow.Pa), 48);
        roster.Columns.Add(Column("Positions", nameof(MyClubRosterRow.Positions), new DataGridLength(1.25, DataGridLengthUnitType.Star), 125));
        ToolGrid.EnableSort(roster, new Dictionary<string, Func<MyClubRosterRow, IComparable?>>
        {
            ["Name"] = row => row.Name,
            ["Age"] = row => row.Age,
            ["CA"] = row => row.Ca,
            ["MCA"] = row => row.Mca,
            ["PA"] = row => row.Pa,
            ["Positions"] = row => row.Positions,
        });

        DataGridTextColumn Column(string title, string property, DataGridLength width, double minWidth) => new()
        {
            Header = ToolGrid.Header(title, title, HorizontalAlignment.Left), Binding = new Binding(property), Width = width, MinWidth = minWidth,
        };
        void Numeric(string title, string property, string sortProperty, double width)
        {
            var col = Column(title, property, new DataGridLength(width), width);
            col.SortMemberPath = sortProperty;
            col.CellStyleClasses.Add("numeric");
            roster.Columns.Add(col);
        }
    }

    void WireEvents()
    {
        clubPicker.SelectionChanged += (_, _) => { if (!updatingPickers) SelectClub((clubPicker.SelectedItem as ClubChoice)?.Club); };
        teamPicker.SelectionChanged += (_, _) => { if (!updatingPickers) SelectTeam((teamPicker.SelectedItem as TeamChoice)?.Team); };
        presetPicker.SelectionChanged += (_, _) => { if (!updatingPickers) LoadSelectedPreset(); };
        search.TextChanged += (_, _) => ApplyRosterFilter();
        roster.SelectionChanged += (_, _) => SelectPlayer((roster.SelectedItem as MyClubRosterRow)?.Player);
        roster.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(roster).Properties.IsLeftButtonPressed
                && e.Source is Visual visual
                && visual.FindAncestorOfType<DataGridRow>() is { DataContext: MyClubRosterRow })
                showPositionTargets = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);

        roster.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (!e.GetCurrentPoint(roster).Properties.IsLeftButtonPressed) return;
            if (e.Source is not Visual visual || visual.FindAncestorOfType<DataGridRow>() is not { DataContext: MyClubRosterRow row }) return;
            pressedRosterRow = row;
            pressPoint = e.GetPosition(roster);
            roster.SelectedItem = row;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        roster.AddHandler(InputElement.PointerMovedEvent, async (object? _, PointerEventArgs e) =>
        {
            if (pressedRosterRow is not { } row || pressPoint is not { } start || !e.GetCurrentPoint(roster).Properties.IsLeftButtonPressed) return;
            Point now = e.GetPosition(roster);
            if (Math.Abs(now.X - start.X) + Math.Abs(now.Y - start.Y) < 9) return;
            pressedRosterRow = null;
            pressPoint = null;
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(PlayerDragFormat, row.Player.Address.ToString("X", Inv));
            data.Add(item);
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            ClearDragPreview();
            showPositionTargets = false;
            RefreshPitch();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        roster.AddHandler(InputElement.PointerReleasedEvent, (object? _, PointerReleasedEventArgs _) => { pressedRosterRow = null; pressPoint = null; }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        DragDrop.SetAllowDrop(pitch, true);
        pitch.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(PlayerDragFormat) is { } text
                && ulong.TryParse(text, NumberStyles.HexNumber, Inv, out ulong address)
                && teamPlayers.FirstOrDefault(p => p.Address == address) is { } player)
            {
                e.DragEffects = DragDropEffects.Move;
                var at = e.GetPosition(pitch);
                if (dragPreview is null)
                {
                    dragPreview = BuildPortrait(player, 42);
                    dragPreview.IsHitTestVisible = false;
                    pitch.Children.Add(dragPreview);
                }
                Canvas.SetLeft(dragPreview, at.X - 21);
                Canvas.SetTop(dragPreview, at.Y - 21);
            }
            else e.DragEffects = DragDropEffects.None;
        });
        pitch.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDragPreview());
        pitch.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            ClearDragPreview();
            if (e.DataTransfer.TryGetValue(PlayerDragFormat) is not { } addressText
                || !ulong.TryParse(addressText, NumberStyles.HexNumber, Inv, out ulong address)) return;
            var player = teamPlayers.FirstOrDefault(p => p.Address == address);
            if (player is null) return;
            var at = e.GetPosition(pitch);
            showPositionTargets = false;
            Place(player, at.X / PitchWidth, at.Y / PitchHeight, null);
            e.Handled = true;
        });

        pitch.PointerMoved += (_, e) =>
        {
            if (draggedMarker is null || !e.GetCurrentPoint(pitch).Properties.IsLeftButtonPressed) return;
            MoveMarker(draggedMarker, e.GetPosition(pitch));
        };
        pitch.PointerReleased += (_, e) =>
        {
            if (draggedMarker is null) return;
            e.Pointer.Capture(null);
            var assignment = assignments.Values.FirstOrDefault(a => ReferenceEquals(a.Marker, draggedMarker));
            draggedMarker = null;
            if (assignment is not null)
            {
                var free = FindFreeCoordinates(assignment.X, assignment.Y, assignment.Player.Address, assignment.Position);
                assignment.X = free.X;
                assignment.Y = free.Y;
                assignment.Position = free.Position;
                showPositionTargets = !draggedMarkerMoved;
                RefreshPitch();
            }
            draggedMarkerMoved = false;
        };
    }

    public void RefreshFromCurrentGame()
    {
        var data = window.MyClubLoadedData;
        if (data is null)
        {
            if (loadedData is not null) ClearGameState();
            emptyState.Text = Loc.S("Load Game to view a club squad.");
            SetUnavailable(true);
            return;
        }
        if (ReferenceEquals(loadedData, data)) return;

        bool sameSession = loadedData is not null && ReferenceEquals(loadedData.Session, data.Session);
        if (sameSession) SaveCurrentComposition();
        else sessionDrafts.Clear();
        string? restorePreset = sameSession ? selectedPresetName : null;
        ulong? priorClubAddress = selectedClub?.Address;
        int priorTeamIndex = selectedClub is not null && selectedTeam is not null ? TeamIndex(selectedClub, selectedTeam) : 0;
        loadedData = data;
        clubs = data.Clubs;
        allRows.Clear();
        teamPlayers = [];
        selectedPlayer = null;
        showPositionTargets = false;
        assignments.Clear();
        selectedClub = null;
        selectedTeam = null;
        selectedPresetName = null;
        updatingPickers = true;
        clubPicker.ItemsSource = clubs.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).Select(c => new ClubChoice(c)).ToArray();
        var wantedAddress = sameSession ? priorClubAddress : data.HumanManagers.FirstOrDefault(m => m.Club is not null)?.Club?.Address;
        selectedClub = clubs.FirstOrDefault(c => c.Address == wantedAddress) ?? clubs.FirstOrDefault();
        clubPicker.SelectedItem = selectedClub is null ? null : clubPicker.Items.Cast<ClubChoice>().FirstOrDefault(c => c.Club.Address == selectedClub.Address);
        updatingPickers = false;
        SelectClub(selectedClub);
        if (sameSession && selectedClub?.Teams.ElementAtOrDefault(priorTeamIndex) is { } priorTeam)
            teamPicker.SelectedItem = teamPicker.Items.Cast<TeamChoice>().FirstOrDefault(c => c.Team.Address == priorTeam.Address);
        if (restorePreset is not null && presetPicker.Items.Cast<PresetChoice>().FirstOrDefault(p => p.Name == restorePreset) is { } priorPreset)
        {
            presetPicker.SelectedItem = priorPreset;
            LoadSelectedPreset();
        }
    }

    void ClearGameState()
    {
        loadedData = null;
        clubs = [];
        teamPlayers = [];
        selectedClub = null;
        selectedTeam = null;
        selectedPlayer = null;
        showPositionTargets = false;
        assignments.Clear();
        sessionDrafts.Clear();
        selectedPresetName = null;
        allRows.Clear();
        updatingPickers = true;
        clubPicker.ItemsSource = null;
        teamPicker.ItemsSource = null;
        presetPicker.ItemsSource = null;
        updatingPickers = false;
        ApplyRosterFilter();
        RefreshPitch();
    }

    void SetUnavailable(bool unavailable)
    {
        roster.IsVisible = !unavailable;
        pitchHost.IsVisible = !unavailable;
        emptyState.IsVisible = unavailable;
        rosterCount.Text = "";
    }

    void SelectClub(ClubEntry? club)
    {
        SaveCurrentComposition();
        selectedClub = club;
        selectedTeam = null;
        selectedPlayer = null;
        showPositionTargets = false;
        selectedPresetName = null;
        teamPlayers = [];
        allRows.Clear();
        assignments.Clear();
        updatingPickers = true;
        var teams = club?.Teams ?? [];
        teamPicker.ItemsSource = teams.Select((team, index) => new TeamChoice(team, TeamName(teams, index))).ToArray();
        var first = club?.FirstTeam ?? teams.FirstOrDefault();
        selectedTeam = first;
        teamPicker.SelectedItem = first is null ? null : teamPicker.Items.Cast<TeamChoice>().FirstOrDefault(c => c.Team.Address == first.Address);
        updatingPickers = false;
        if (selectedTeam is not null) LoadTeam(selectedTeam);
        else ShowEmpty(Loc.S("No team available for this club."));
        RefreshPresets();
    }

    void SelectTeam(TeamEntry? team)
    {
        if (selectedTeam?.Address == team?.Address) return;
        SaveCurrentComposition();
        selectedTeam = team;
        selectedPlayer = null;
        showPositionTargets = false;
        selectedPresetName = null;
        teamPlayers = [];
        allRows.Clear();
        assignments.Clear();
        if (team is not null) LoadTeam(team);
        else ShowEmpty(Loc.S("No team available for this club."));
        RefreshPresets();
    }

    void LoadTeam(TeamEntry team)
    {
        try
        {
            teamPlayers = window.MyClubSession.ReadSquad(team.Address);
            allRows.Clear();
            allRows.AddRange(teamPlayers.OrderByDescending(p => p.CurrentAbility).Select(p => new MyClubRosterRow(p)));
            if (teamPlayers.Count == 0)
            {
                ShowEmpty(Loc.S("No players found for this team."));
                return;
            }
            SetUnavailable(false);
            emptyState.IsVisible = false;
            ApplyRosterFilter();
            RestoreCurrentComposition();
            RefreshPitch();
        }
        catch (Exception ex)
        {
            teamPlayers = [];
            allRows.Clear();
            ShowEmpty(UserMessage.For(ex));
        }
    }

    static string TeamName(IReadOnlyList<TeamEntry> teams, int index) =>
        teams[index].IsFirstTeam ? Loc.S("First team") : teams.Count == 2 ? Loc.S("Reserves")
        : teams.Count == 3 ? index == 1 ? Loc.S("U21") : Loc.S("U18") : Loc.F("Team {0}", index + 1);

    void ShowEmpty(string message)
    {
        roster.IsVisible = false;
        pitchHost.IsVisible = false;
        emptyState.Text = message;
        emptyState.IsVisible = true;
        if (!contentGrid.Children.Contains(emptyState)) contentGrid.Children.Add(emptyState);
        rosterCount.Text = "";
    }

    void ApplyRosterFilter()
    {
        var query = MainWindow.Fold(search.Text?.Trim() ?? "");
        var visible = query.Length == 0 ? allRows : allRows.Where(r => MainWindow.Fold($"{r.Name} {r.Positions}").Contains(query, StringComparison.Ordinal)).ToList();
        ToolGrid.SetItems(roster, visible);
        rosterCount.Text = allRows.Count == 0 ? "" : Loc.F("{0:N0} players", visible.Count);
    }

    void SelectPlayer(PlayerRecord? player)
    {
        selectedPlayer = player;
        showPositionTargets = player is not null;
        RefreshPitch();
    }

    void Place(PlayerRecord player, double? x, double? y, PlayerPosition? chosenPosition)
    {
        if (!assignments.ContainsKey(player.Address) && assignments.Count >= MaxPitchPlayers)
        {
            window.ShowMyClubMessage(Loc.F("A composition can contain at most {0} players.", MaxPitchPlayers));
            return;
        }
        var desired = chosenPosition is { } pos
            ? NearestPitchSlot(pos, x ?? PitchSlots.First(p => p.Position == pos).X, y ?? PitchSlots.First(p => p.Position == pos).Y)
            : NearestPitchSlot(x ?? .5, y ?? .5);
        var free = FindFreeCoordinates(desired.X, desired.Y, player.Address, chosenPosition);
        var assignment = assignments.GetValueOrDefault(player.Address);
        if (assignment is null) assignments[player.Address] = assignment = new(player, free.Position, free.X, free.Y);
        assignment.Player = player;
        assignment.Position = free.Position;
        assignment.X = free.X;
        assignment.Y = free.Y;
        selectedPlayer = player;
        showPositionTargets = false;
        RefreshPitch();
    }

    (double X, double Y, PlayerPosition Position) FindFreeCoordinates(double x, double y, ulong exceptAddress, PlayerPosition? preferredPosition = null)
    {
        var choices = (preferredPosition is { } position ? PitchSlots.Where(p => p.Position == position) : PitchSlots)
            .OrderBy(p => Math.Pow((p.X - x) * PitchWidth, 2) + Math.Pow((p.Y - y) * PitchHeight, 2));
        foreach (var slot in choices)
            if (assignments.Values.Where(a => a.Player.Address != exceptAddress)
                .All(a => Math.Abs(slot.X - a.X) * PitchWidth >= MarkerWidth + 4
                    || Math.Abs(slot.Y - a.Y) * PitchHeight >= MarkerHeight + 4))
                return (slot.X, slot.Y, slot.Position);
        var fallback = choices.First();
        return (fallback.X, fallback.Y, fallback.Position);
    }

    static MyClubPitchSlot NearestPitchSlot(double x, double y) => PitchSlots
        .MinBy(p => Math.Pow((p.X - x) * PitchWidth, 2) + Math.Pow((p.Y - y) * PitchHeight, 2))!;

    static MyClubPitchSlot NearestPitchSlot(PlayerPosition position, double x, double y) => PitchSlots
        .Where(p => p.Position == position)
        .MinBy(p => Math.Pow((p.X - x) * PitchWidth, 2) + Math.Pow((p.Y - y) * PitchHeight, 2))!;

    void RefreshPitch()
    {
        foreach (var marker in pitchMarkers) pitch.Children.Remove(marker);
        pitchMarkers.Clear();
        ClearPositionSpots();
        if (showPositionTargets) AddPositionSpots();
        foreach (var item in assignments.Values)
        {
            var mca = McaCalculator.IsGoalkeeper(item.Player) ? "—" : ScoutingMetrics.McaRounded(item.Player).ToString(Inv);
            var text = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(BuildPortrait(item.Player, 34));
            text.Children.Add(new TextBlock { Text = item.Player.DisplayName, FontSize = 9, Foreground = UiKit.TextPrimary, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = MarkerWidth - 6 });
            text.Children.Add(new TextBlock { Text = mca, FontSize = 9.5, FontWeight = FontWeight.SemiBold, Foreground = UiKit.TextPrimary, HorizontalAlignment = HorizontalAlignment.Center });
            var marker = new Border
            {
                Width = MarkerWidth, Height = MarkerHeight, Padding = new Thickness(4, 3), CornerRadius = new CornerRadius(5),
                Background = UiKit.Brush("#111827"), BorderBrush = UiKit.Brush("#334155"),
                BorderThickness = new Thickness(1), Child = text,
            };
            item.Marker = marker;
            marker.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(marker).Properties.IsRightButtonPressed)
                {
                    assignments.Remove(item.Player.Address);
                    if (selectedPlayer?.Address == item.Player.Address) selectedPlayer = null;
                    showPositionTargets = false;
                    RefreshPitch();
                    e.Handled = true;
                    return;
                }
                if (!e.GetCurrentPoint(marker).Properties.IsLeftButtonPressed) return;
                selectedPlayer = item.Player;
                showPositionTargets = true;
                AddPositionSpots();
                draggedMarkerMoved = false;
                draggedMarker = marker;
                e.Pointer.Capture(marker);
                e.Handled = true;
            };
            Canvas.SetLeft(marker, item.X * PitchWidth - MarkerWidth / 2);
            Canvas.SetTop(marker, item.Y * PitchHeight - MarkerHeight / 2);
            pitch.Children.Add(marker);
            pitchMarkers.Add(marker);
        }
    }

    void ClearPositionSpots()
    {
        foreach (var spot in positionSpots) pitch.Children.Remove(spot);
        positionSpots.Clear();
    }

    void AddPositionSpots()
    {
        ClearPositionSpots();
        if (!showPositionTargets || selectedPlayer is not { } chosen) return;
        int insertAt = pitch.Children.Count - pitchMarkers.Count;
        foreach (var spot in PitchSlots)
        {
            int rating = chosen.Position(spot.Position);
            bool playable = rating > 10;
            var target = new Button
            {
                Content = PositionLabels.Short(spot.Position),
                Width = 38, Height = 24, MinWidth = 38, MinHeight = 24,
                Padding = new Thickness(1), FontSize = 9.5, FontWeight = FontWeight.SemiBold,
                CornerRadius = new CornerRadius(5),
                Background = UiKit.Brush(playable ? "#174E3C" : "#14231F"),
                Foreground = UiKit.Brush(playable ? "#BBF7D0" : "#6B8B80"),
                BorderBrush = UiKit.Brush(playable ? "#4ADE80" : "#31554A"),
                BorderThickness = new Thickness(playable ? 2 : 1), Opacity = playable ? 1 : .72,
            };
            ToolTip.SetTip(target, Loc.F("{0}: {1}/20", Loc.S(PitchLayout.Positions.First(p => p.Position == spot.Position).Label), rating));
            target.Click += (_, _) =>
            {
                if (selectedPlayer is { } player) Place(player, spot.X, spot.Y, spot.Position);
            };
            Canvas.SetLeft(target, spot.X * PitchWidth - 19);
            Canvas.SetTop(target, spot.Y * PitchHeight - 12);
            pitch.Children.Insert(insertAt++, target);
            positionSpots.Add(target);
        }
    }

    void ClearDragPreview()
    {
        if (dragPreview is null) return;
        pitch.Children.Remove(dragPreview);
        dragPreview = null;
    }

    void MoveMarker(Control marker, Point point)
    {
        var assignment = assignments.Values.FirstOrDefault(a => ReferenceEquals(a.Marker, marker));
        if (assignment is null) return;
        var slot = NearestPitchSlot(point.X / PitchWidth, point.Y / PitchHeight);
        var free = FindFreeCoordinates(slot.X, slot.Y, assignment.Player.Address, slot.Position);
        if (Math.Abs(assignment.X - free.X) > .001 || Math.Abs(assignment.Y - free.Y) > .001) draggedMarkerMoved = true;
        assignment.X = free.X;
        assignment.Y = free.Y;
        assignment.Position = free.Position;
        Canvas.SetLeft(marker, assignment.X * PitchWidth - MarkerWidth / 2);
        Canvas.SetTop(marker, assignment.Y * PitchHeight - MarkerHeight / 2);
    }

    Control BuildPortrait(PlayerRecord player, double size)
    {
        var initials = string.Join("", player.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
        var fallback = new TextBlock
        {
            Text = initials, FontSize = Math.Max(10, size * .32), FontWeight = FontWeight.SemiBold,
            Foreground = UiKit.TextSecondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var image = window.MyClubGraphics.Image("person", player.Id, size);
        fallback.IsVisible = image.Source is null;
        image.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty) fallback.IsVisible = image.Source is null;
        };
        var content = new Grid();
        content.Children.Add(fallback);
        content.Children.Add(image);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), ClipToBounds = true,
            Background = UiKit.SurfaceAlt, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    void RemoveSelected()
    {
        if (selectedPlayer is null || !assignments.Remove(selectedPlayer.Address)) return;
        RefreshPitch();
    }

    void RefreshPresets()
    {
        if (selectedClub is null || selectedTeam is null)
        {
            updatingPickers = true;
            presetPicker.ItemsSource = null;
            presetPicker.SelectedItem = null;
            updatingPickers = false;
            return;
        }
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        var options = MyClubPresetStore.Read().Where(p => p.ClubTeamKey == key).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => new PresetChoice(p.Name)).ToArray();
        updatingPickers = true;
        presetPicker.ItemsSource = options;
        var current = options.FirstOrDefault(p => p.Name == selectedPresetName);
        presetPicker.SelectedItem = current;
        if (current is null) selectedPresetName = null;
        updatingPickers = false;
    }

    void SaveCurrentComposition()
    {
        if (selectedClub is null || selectedTeam is null || teamPlayers.Count == 0) return;
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        sessionDrafts[key] = assignments.Values.Where(a => a.Player.Id is not null)
            .Select(a => new MyClubPresetPlayer(a.Player.Id!.Value, a.Position, a.X, a.Y)).ToList();
    }

    void RestoreCurrentComposition()
    {
        if (selectedClub is null || selectedTeam is null) return;
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        if (!sessionDrafts.TryGetValue(key, out var saved)) return;
        var byId = teamPlayers.Where(p => p.Id is not null).ToDictionary(p => p.Id!.Value);
        foreach (var slot in saved)
            if (byId.TryGetValue(slot.PlayerId, out var player))
            {
                if (assignments.Count >= MaxPitchPlayers) break;
                var free = FindFreeCoordinates(slot.X, slot.Y, player.Address, slot.Position);
                assignments[player.Address] = new(player, slot.Position, free.X, free.Y);
            }
    }

    void SavePreset()
    {
        if (selectedClub is null || selectedTeam is null) return;
        string name = presetName.Text?.Trim() ?? "";
        if (name.Length == 0) { window.ShowMyClubMessage(Loc.S("Enter a preset name.")); return; }
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        var all = MyClubPresetStore.Read().Where(p => !(p.ClubTeamKey == key && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
        var saved = new MyClubPreset(key, name, assignments.Values
            .Where(a => a.Player.Id is not null)
            .Select(a => new MyClubPresetPlayer(a.Player.Id!.Value, a.Position, a.X, a.Y)).ToList());
        all.Add(saved);
        try
        {
            MyClubPresetStore.Write(all);
            selectedPresetName = name;
            RefreshPresets();
            presetPicker.SelectedItem = presetPicker.Items.Cast<PresetChoice>().FirstOrDefault(p => p.Name == name);
            window.ShowMyClubMessage(Loc.S("Preset saved on this computer."));
        }
        catch (Exception ex) { window.ShowMyClubMessage(UserMessage.For(ex)); }
    }

    void DeletePreset()
    {
        if (selectedClub is null || selectedTeam is null || presetPicker.SelectedItem is not PresetChoice choice) return;
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        try
        {
            MyClubPresetStore.Write(MyClubPresetStore.Read().Where(p => !(p.ClubTeamKey == key && p.Name == choice.Name)).ToList());
            selectedPresetName = null;
            RefreshPresets();
            window.ShowMyClubMessage(Loc.S("Preset deleted."));
        }
        catch (Exception ex) { window.ShowMyClubMessage(UserMessage.For(ex)); }
    }

    void LoadSelectedPreset()
    {
        if (presetPicker.SelectedItem is not PresetChoice choice || selectedClub is null || selectedTeam is null) return;
        var key = ClubTeamKey(selectedClub, TeamIndex(selectedClub, selectedTeam));
        var preset = MyClubPresetStore.Read().FirstOrDefault(p => p.ClubTeamKey == key && p.Name == choice.Name);
        if (preset is null) return;
        assignments.Clear();
        var byId = teamPlayers.Where(p => p.Id is not null).ToDictionary(p => p.Id!.Value);
        foreach (var slot in preset.Players)
            if (byId.TryGetValue(slot.PlayerId, out var player))
            {
                if (assignments.Count >= MaxPitchPlayers) break;
                var free = FindFreeCoordinates(slot.X, slot.Y, player.Address);
                assignments[player.Address] = new(player, slot.Position, free.X, free.Y);
            }
        selectedPresetName = preset.Name;
        SaveCurrentComposition();
        selectedPlayer = null;
        showPositionTargets = false;
        RefreshPitch();
    }

    static string ClubTeamKey(ClubEntry club, int teamIndex) => $"{club.Id?.ToString(Inv) ?? MainWindow.Fold(club.DisplayName)}:{teamIndex}";
    static int TeamIndex(ClubEntry club, TeamEntry team)
    {
        for (int i = 0; i < club.Teams.Count; i++) if (club.Teams[i].Address == team.Address) return i;
        return 0;
    }
}

internal sealed record MyClubPreset(string ClubTeamKey, string Name, IReadOnlyList<MyClubPresetPlayer> Players);
internal sealed record MyClubPresetPlayer(int PlayerId, PlayerPosition Position, double X, double Y);

internal static class MyClubPresetStore
{
    static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fm27Editor", "my-club-presets.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static IReadOnlyList<MyClubPreset> Read()
    {
        try { return File.Exists(PathName) ? JsonSerializer.Deserialize<List<MyClubPreset>>(File.ReadAllText(PathName), Options) ?? [] : []; }
        catch (Exception) { return []; }
    }
    public static void Write(IReadOnlyList<MyClubPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        File.WriteAllText(PathName, JsonSerializer.Serialize(presets, Options));
    }
}
