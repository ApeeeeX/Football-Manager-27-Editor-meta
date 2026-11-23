using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    int selectedSquadTeamIndex;
    SquadPlayerRow? selectedSquadRow;

    /// <summary>The name a squad tab uses for the i-th of a club's ordered teams: First team, Reserves, U21, U18 or Team n.</summary>
    static string SquadTeamLabel(IReadOnlyList<TeamEntry> orderedTeams, int i)
    {
        var t = orderedTeams[i];
        if (t.IsFirstTeam) return Loc.S("First team");
        if (orderedTeams.Count == 2) return Loc.S("Reserves");
        if (orderedTeams.Count == 3) return i == 1 ? Loc.S("U21") : Loc.S("U18");
        return Loc.F("Team {0}", i + 1);
    }

    bool HookClubSquadsView(ClubEntry club, IReadOnlyList<TeamEntry> orderedTeams, TeamEntry? activeTeam, ulong teamAddr)
    {
        if (selectedClubSection != ClubSection.Squads)
            return false;

        if (orderedTeams.Count == 0)
        {
            clubEditorPanel.Children.Add(new TextBlock
            {
                Text = Loc.S("No teams available for this club."),
                Foreground = Brush("#94A3B8"),
                FontSize = 13,
                Margin = new Thickness(4, 12, 0, 0),
            });
            return true;
        }

        if (selectedSquadTeamIndex >= orderedTeams.Count)
            selectedSquadTeamIndex = 0;

        // Team options use the app's own tab strip (same language as the module row), not bespoke pills.
        var teamPages = new List<(string Title, Control Content)>();
        for (int i = 0; i < orderedTeams.Count; i++)
            teamPages.Add(($"{SquadTeamLabel(orderedTeams, i)} ({orderedTeams[i].SquadCount})", new Panel()));
        var teamBar = UiKit.OptionTabs(teamPages, selectedSquadTeamIndex, index =>
        {
            selectedSquadTeamIndex = index;
            selectedSquadRow = null;
            RenderClubEditor();
        });
        teamBar.Margin = new Thickness(0, 0, 0, 10);
        clubEditorPanel.Children.Add(teamBar);

        var curTeam = orderedTeams[selectedSquadTeamIndex];
        var allLoadedPlayers = rows.OfType<PlayerRecord>().ToList();
        Func<IReadOnlyList<PlayerRecord>> getSquad = () =>
            SquadAdapter.ReadSquad(backend.Session, curTeam.Address, allLoadedPlayers);

        Action? refreshSquad = null;

        // Golden Generation is offered on the first team and works on every other team of the club (reserves, U21, U18...).
        Func<IReadOnlyList<PlayerRecord>>? youthPlayers = selectedSquadTeamIndex == 0 && orderedTeams.Count > 1
            ? () => orderedTeams.Skip(1).SelectMany(t => SquadAdapter.ReadSquad(backend.Session, t.Address, allLoadedPlayers)).DistinctBy(p => p.Address).ToList()
            : null;
        var layoutGrid = BuildSquadGridAndActions(getSquad, isNation: false, out refreshSquad, youthPlayers, club.Address);
        clubEditorPanel.Children.Add(new Border { Margin = new Thickness(8, 0, 0, 0), Child = layoutGrid });
        return true;
    }

    Control BuildSquadGridAndActions(
        Func<IReadOnlyList<PlayerRecord>> getSquadPlayers,
        bool isNation,
        out Action refreshSquad,
        Func<IReadOnlyList<PlayerRecord>>? youthPlayers = null,
        ulong? squadClub = null)
    {
        IReadOnlyList<PlayerRecord> currentSquadPlayers = [];

        List<SquadPlayerRow> BuildRows(IReadOnlyList<PlayerRecord> players)
        {
            var playerFields = players.ToDictionary(
                p => p.Address,
                p => SquadAdapter.ReadAllPlayerFields(backend.Session, p));

            DateOnly? gameDate = backend.Session.ReadGameDate();
            var list = new List<SquadPlayerRow>(players.Count);
            foreach (var p in players)
            {
                var fields = playerFields.GetValueOrDefault(p.Address);
                long? cond = fields?.TryGetValue(PlayerField.Condition, out long c) == true ? c : null;
                long? price = fields?.TryGetValue(PlayerField.AskingPrice, out long pr) == true ? pr : null;

                var injuries = backend.Session.ReadInjuries(p);
                var bans = backend.Session.ReadBans(p);
                var unhappy = gameDate is { } today ? backend.Session.ReadUnhappiness(p).Where(m => m.IsActive(today)).ToList() : [];
                bool isInjured = injuries.Count > 0;
                bool isBanned = bans.Count > 0;
                bool isFrozen = backend.Freezer?.Players.Any(fp => fp.Id == p.Id) == true;
                string inf = SquadFormatters.FormatInfFlags(p, isInjured, isBanned, unhappy.Count > 0, isFrozen);
                var tips = new List<string>();
                if (isFrozen) tips.Add(Loc.S("Frozen"));
                if (isInjured)
                {
                    tips.Add(string.Join(", ", injuries.Select(injury => !string.IsNullOrEmpty(injury.Name)
                        ? char.ToUpper(injury.Name[0]) + injury.Name[1..]
                        : Loc.S("Injured"))));
                }
                if (isBanned)
                {
                    tips.Add(string.Join(", ", bans.Select(b => b.Matches > 0 ? Loc.F("{0} match ban", b.Matches) : Loc.S("Ban"))));
                }
                if (unhappy.Count > 0) tips.Add(string.Join(", ", unhappy.Select(m => UnhappinessLabels.Motive(m.Motive))));
                string infTip = string.Join("; ", tips);

                string pos = SquadFormatters.FormatPositions(p);
                int? squadNum = SquadAdapter.ReadSquadNumber(backend.Session, p);
                list.Add(new SquadPlayerRow(p, squadNum, inf, pos, cond, price, "—", infTip));
            }
            return list;
        }

        List<SquadPlayerRow> squadRows = [];

        var squadGrid = ToolGrid.Create();
        squadGrid.ItemsSource = squadRows;

        Control Header(string text, string tooltip, HorizontalAlignment align = HorizontalAlignment.Center) => ToolGrid.Header(text, tooltip, align);
        DataGridTemplateColumn NumCol(string title, Func<SquadPlayerRow, string> selector, double width, string tooltip) => ToolGrid.Number(title, selector, width, tooltip);

        // Tools act on every ticked player; all are ticked until the user unticks someone.
        var excluded = new HashSet<ulong>();
        Action? updateSelection = null;
        ToolGrid.AddTick<SquadPlayerRow>(squadGrid, excluded, r => r.Player.Address, () => squadRows, () => updateSelection?.Invoke());
        squadGrid.Columns.Add(NumCol("#", r => r.DisplayNumber, 30, "Squad Number"));

        squadGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = Header("Inf", "Status / Injuries / Bans"),
            Width = new DataGridLength(40),
            CellTemplate = new FuncDataTemplate<SquadPlayerRow>((row, _) =>
            {
                var tb = new TextBlock
                {
                    Text = row.InfFlags,
                    Foreground = Brush("#EF4444"),
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.None,
                };
                if (!string.IsNullOrEmpty(row.InfTooltip))
                    ToolTip.SetTip(tb, row.InfTooltip);
                return tb;
            }),
        });

        squadGrid.Columns.Add(ToolGrid.StarText<SquadPlayerRow>("Name", r => r.DisplayName, 1.5, 118, "Player Name", "#F1F5F9", bold: true));
        squadGrid.Columns.Add(ToolGrid.StarText<SquadPlayerRow>("Pos", r => r.FormattedPositions, 0.9, 82, "Positions", "#C4B5FD"));
        if (isNation)
            squadGrid.Columns.Add(ToolGrid.StarText<SquadPlayerRow>("Club", r => r.DisplayClub, 1.0, 96, "Club"));
        else
            squadGrid.Columns.Add(ToolGrid.StarText<SquadPlayerRow>("Nat", r => r.DisplayNat, 0.8, 72, "Nationality"));

        squadGrid.Columns.Add(NumCol("CA", r => r.DisplayCa, 36, "Current Ability"));
        squadGrid.Columns.Add(NumCol("PA", r => r.DisplayPa, 36, "Potential Ability"));
        squadGrid.Columns.Add(NumCol("Age", r => r.DisplayAge, 36, "Age"));
        squadGrid.Columns.Add(NumCol("Cond", r => r.DisplayCondition, 48, "Condition (0–10000)"));

        if (!isNation)
        {
            squadGrid.Columns.Add(ToolGrid.Number<SquadPlayerRow>("Price", r => r.DisplayAskingPrice, 86, "Asking Price (£)",
                row => string.IsNullOrEmpty(row.DisplayAskingPrice) ? null : $"£{row.DisplayAskingPrice}"));
        }

        squadGrid.DoubleTapped += (_, e) =>
        {
            if (UiKit.OnRow(e) && squadGrid.SelectedItem is SquadPlayerRow r)
                OpenPlayerFromSquad(r.Player);
        };
        squadGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && squadGrid.SelectedItem is SquadPlayerRow r)
            {
                OpenPlayerFromSquad(r.Player);
                e.Handled = true;
            }
        };
        AttachPlayerContextMenu(squadGrid, item => (item as SquadPlayerRow)?.Player);

        // Header click sorts like the Players table: numbers start high to low, text starts A to Z, a second click reverses.
        // Rows with no value (blank number, no age) stay at the bottom either way.
        var sortKeys = new Dictionary<string, Func<SquadPlayerRow, IComparable?>>
        {
            ["#"] = r => r.SquadNumber is > 0 ? r.SquadNumber.Value : null,
            ["Inf"] = r => r.InfFlags.Length > 0 ? r.InfFlags : null,
            ["Name"] = r => r.DisplayName.ToLowerInvariant(),
            ["Pos"] = r => r.FormattedPositions.Length > 0 ? r.FormattedPositions.ToLowerInvariant() : null,
            ["Club"] = r => r.DisplayClub.ToLowerInvariant(),
            ["Nat"] = r => r.DisplayNat.ToLowerInvariant(),
            ["CA"] = r => r.Player.CurrentAbility,
            ["PA"] = r => r.Player.PotentialAbility,
            ["Age"] = r => r.Player.Age,
            ["Cond"] = r => r.Condition is >= 0 ? r.Condition : null,
            ["Price"] = r => r.AskingPrice is >= 0 ? r.AskingPrice : null,
        };
        void SetRows(List<SquadPlayerRow> rows, HashSet<ulong> selected)
        {
            squadRows = rows;
            ToolGrid.SetItems(squadGrid, squadRows);
            var first = rows.FirstOrDefault(r => selected.Contains(r.Player.Address));
            if (first is not null)
            {
                squadGrid.SelectedItem = first;
                foreach (var r in rows)
                {
                    if (r != first && selected.Contains(r.Player.Address))
                        squadGrid.SelectedItems.Add(r);
                }
            }
            updateSelection?.Invoke();
        }
        ToolGrid.EnableSort(squadGrid, sortKeys);

        // Reading a squad is one memory read per player per field, so it runs in the background and only the
        // newest load is shown; the grid stays empty (and the window responsive) until its rows arrive.
        int squadLoadVersion = 0;
        async void LoadSquadRows(bool keepSelection)
        {
            int version = ++squadLoadVersion;
            var prevSelected = keepSelection
                ? squadGrid.SelectedItems.OfType<SquadPlayerRow>().Select(r => r.Player.Address).ToHashSet()
                : new HashSet<ulong>();
            try
            {
                var loaded = await Task.Run(() =>
                {
                    var players = getSquadPlayers();
                    return (Players: players, Rows: BuildRows(players));
                });
                if (version != squadLoadVersion) return;
                currentSquadPlayers = loaded.Players;
                SetRows(loaded.Rows, prevSelected);
            }
            catch (Exception ex) { if (version == squadLoadVersion) status.Text = UserMessage.For(ex); }
        }
        void RefreshSquadLocal() => LoadSquadRows(keepSelection: true);
        refreshSquad = RefreshSquadLocal;
        LoadSquadRows(keepSelection: false);

        var actionsCard = CreatePlayerActionsPanel(
            player: null,
            isSquadMode: true,
            getSquadPlayers: () => currentSquadPlayers,
            onStatus: msg => status.Text = msg,
            getSelectedPlayers: () => squadRows.Where(r => !excluded.Contains(r.Player.Address)).Select(r => r.Player).ToArray(),
            onApplied: RefreshSquadLocal,
            registerSelectionUpdater: updater => updateSelection = updater,
            getYouthPlayers: youthPlayers,
            squadClub: squadClub);

        squadGrid.SelectionChanged += (_, _) =>
        {
            selectedSquadRow = squadGrid.SelectedItem as SquadPlayerRow;
            updateSelection?.Invoke();
        };

        return ToolGrid.WithTools(squadGrid, actionsCard);
    }

    async void OpenPlayerFromSquad(PlayerRecord player)
    {
        ulong addr = player.Address;
        int? id = player.Id;

        if (rows.Count == 0 && backend.State.Status == ConnectionStatus.Connected)
            await LoadAsync();

        var target = rows.OfType<PlayerRecord>()
            .FirstOrDefault(p => p.Address == addr || (id is not null && p.Id == id))
            ?? player;

        updating = true;
        selected = target;
        grid.SelectedItem = target;
        updating = false;
        ShowDetailTab(target);
        status.Text = Loc.F("Opened {0}", target.DisplayName);
    }
}
