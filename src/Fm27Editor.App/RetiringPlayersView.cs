using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.App;

/// <summary>
/// "View Players Retirements": everyone the game has planned to retire (players and staff), as FMRTE's Retire Date list shows
/// them: name, age, nation, club, retirement date. Opened as its own tab from the toolbar.
/// </summary>
public sealed partial class MainWindow
{
    public sealed record RetiringRow(string Name, int? Age, string Nation, string Club, DateOnly? RetirementDate, PlayerRecord? Player, StaffRecord? Staff)
    {
        public string AgeText => Age?.ToString(CultureInfo.InvariantCulture) ?? "";
        public string DateText => RetirementDate is { } d ? DateSetting.Format(d) : "—";
    }

    DataGrid? retiringGrid;
    TextBox? retiringSearch;
    TextBlock? retiringCountLabel;
    Control? retiringView;
    List<RetiringRow> retiringAllRows = [];
    bool retiringDirty = true;

    Control BuildRetiringView()
    {
        retiringSearch = new TextBox { Watermark = Loc.S("Search retiring…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        retiringSearch.TextChanged += (_, _) => ApplyRetiringFilter();
        retiringCountLabel = new TextBlock { Foreground = UiKit.TextMuted, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(retiringCountLabel, 1);
        top.Children.Add(retiringSearch);
        top.Children.Add(retiringCountLabel);
        var filterBar = new Border
        {
            Background = UiKit.Surface, BorderBrush = UiKit.BorderSubtle, BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 7), Child = top,
        };

        retiringGrid = ToolGrid.Create();
        ConfigureRetiringColumns();
        void OpenRow(RetiringRow row)
        {
            if (row.Player is { } p) ShowDetailTab(p);
            else if (row.Staff is { } s) ((IScreenHost)this).OpenStaff(s);
        }
        retiringGrid.DoubleTapped += (_, e) => { if (UiKit.OnRow(e) && retiringGrid.SelectedItem is RetiringRow row) OpenRow(row); };
        retiringGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && retiringGrid.SelectedItem is RetiringRow row) { OpenRow(row); e.Handled = true; }
        };
        AttachPlayerContextMenu(retiringGrid, item => (item as RetiringRow)?.Player, RefreshRetiringPlayers);

        var dock = new DockPanel { Background = UiKit.WindowBg };
        DockPanel.SetDock(filterBar, Dock.Top);
        dock.Children.Add(filterBar);
        dock.Children.Add(retiringGrid);
        return dock;
    }

    void ConfigureRetiringColumns()
    {
        if (retiringGrid is null) return;
        retiringGrid.Columns.Clear();
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Name", r => r.Name, 210, "Player name", bold: true));
        retiringGrid.Columns.Add(ToolGrid.Number<RetiringRow>("Age", r => r.AgeText, 52, "Age"));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Nation", r => r.Nation, 140, "Nation"));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Club", r => r.Club, 190, "Current club"));
        retiringGrid.Columns.Add(ToolGrid.Text<RetiringRow>("Retire Date", r => r.DateText, 0, "Planned retirement date", minWidth: 110));
        ToolGrid.EnableSort(retiringGrid, new Dictionary<string, Func<RetiringRow, IComparable?>>
        {
            ["Name"] = r => r.Name.ToLowerInvariant(),
            ["Age"] = r => r.Age,
            ["Nation"] = r => r.Nation.ToLowerInvariant(),
            ["Club"] = r => r.Club.Length > 0 ? r.Club.ToLowerInvariant() : null,
            ["Retire Date"] = r => r.RetirementDate,
        });
    }

    void InvalidateRetiringPlayers() => retiringDirty = true;

    void ShowLoadedRetirements()
    {
        if (retiringDirty) RefreshRetiringPlayers();
        else ApplyRetiringFilter();
    }

    List<RetiringRow> ReadRetiringRows(LoadedGameData? data = null)
    {
        var session = data?.Session ?? backend.Session;
        // The retirement reader validates its vector twice against live memory. Cache only the subsequent person lookups.
        var reader = new PlannedRetirements(session.Memory, session.Profile, session.ModuleBase);
        if (!reader.IsSupported) return [];
        var planned = reader.ReadAll();
        IReadOnlyList<PlayerRecord> players = data?.Players ?? rows.OfType<PlayerRecord>().ToArray();
        if (data is null && players.Count == 0) players = session.Players.LoadAll();
        bool canUseCachedPersonIndex = session.Profile.Fields.PersonIndex == WorkPermitTable.PersonIndexFor(session.Profile);
        BulkReadScope? reads = null;
        PlannedRetirements? personReader = null;
        try
        {
            var result = new List<RetiringRow>();
            var found = new HashSet<uint>();
            foreach (var p in players)
            {
                uint person;
                if (canUseCachedPersonIndex && p.Id is > 0 && p.PersonIndex is { } pi && session.Profile.PlayerVtableRvas.Contains(p.VtableRva))
                {
                    person = (uint)pi;
                }
                else
                {
                    reads ??= new BulkReadScope(session.Memory);
                    personReader ??= new PlannedRetirements(reads.Memory, session.Profile, session.ModuleBase);
                    try { person = personReader.PersonId(p); } catch (MemoryAccessException) { continue; } catch (StaleObjectException) { continue; }
                }
                if (!planned.TryGetValue(person, out var plan) || !found.Add(person)) continue;
                result.Add(new(p.DisplayName, p.Age, p.NationalityName, p.ClubName, plan.Date, p, null));
            }
            if (found.Count < planned.Count)
            {
                var today = data?.GameDate ?? session.ReadGameDate();
                foreach (var s in data?.Staff ?? session.Staff.LoadAll())
                {
                    uint person;
                    if (s.PersonIndex is { } index)
                    {
                        person = (uint)index;
                    }
                    else
                    {
                        reads ??= new BulkReadScope(session.Memory);
                        person = BitConverter.ToUInt32(reads.Memory.Read(s.Address + 0x8, 4));
                    }
                    if (!planned.TryGetValue(person, out var plan) || !found.Add(person)) continue;
                    int? age = s.DateOfBirth is { } born && today is { } d ? d.Year - born.Year - (d < born.AddYears(d.Year - born.Year) ? 1 : 0) : null;
                    result.Add(new(s.DisplayName, age, s.Nationality?.Name ?? "", s.Club?.ShortName ?? s.Club?.Name ?? "", plan.Date, null, s));
                }
            }
            return result.OrderBy(r => r.RetirementDate ?? DateOnly.MaxValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { reads?.Dispose(); }
    }

    void RefreshRetiringPlayers()
    {
        if (backend.State.Status != ConnectionStatus.Connected) { retiringAllRows = []; ApplyRetiringFilter(); return; }
        try
        {
            retiringAllRows = ReadRetiringRows();
            retiringDirty = false;
            ApplyRetiringFilter();
        }
        catch (Exception ex) { status.Text = UserMessage.For(ex); }
    }

    void ApplyRetiringFilter()
    {
        if (retiringGrid is null) return;
        string q = (retiringSearch?.Text ?? "").Trim();
        var filtered = retiringAllRows.Where(r => q.Length == 0
            || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Club.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Nation.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ToolGrid.SetItems(retiringGrid, filtered);
        if (retiringCountLabel is not null)
            retiringCountLabel.Text = filtered.Count == retiringAllRows.Count
                ? Loc.F("Showing {0:N0} retiring players", filtered.Count)
                : Loc.F("Showing {0:N0} of {1:N0} retiring players", filtered.Count, retiringAllRows.Count);
    }

    void RefreshRetiringLocalization()
    {
        if (retiringSearch is not null) retiringSearch.Watermark = Loc.S("Search retiring…");
        ConfigureRetiringColumns();
        ApplyRetiringFilter();
    }
}
