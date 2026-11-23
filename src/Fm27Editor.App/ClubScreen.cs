using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Threading;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

/// <summary>What the club screen still asks of the window: the section panels and the kit, squad and affiliation drafts live there.</summary>
internal interface IClubPanels
{
    /// <summary>The list of sections (Information, Squads, Finances...) shown down the left of a club.</summary>
    ListBox SectionRail { get; }
    IReadOnlyList<ClubEntry>? CachedClubs { get; }
    IReadOnlyList<PlayerRecord> ShownPlayers { get; }
    /// <summary>The clubs were read: the window keeps them for the other screens and shows the game date.</summary>
    void ClubsLoaded(IReadOnlyList<ClubEntry> clubs, IReadOnlyList<PlayerRecord>? freshPlayers, DateOnly? gameDate);
    /// <summary>The game connection changed: drop what belongs to the old connection.</summary>
    void Reconnected();
    /// <summary>Draws the club's header and the open section into the screen.</summary>
    void BuildClub(ClubEntry club, ClubDraft draft);
    /// <summary>Kit, tactical familiarity or affiliation edits waiting for Save.</summary>
    bool HasPending(ClubEntry club);
    /// <summary>Writes the pending kit, familiarity, affiliation and field edits; returns how many changes were written.</summary>
    int SaveClub(ClubEntry club, ClubDraft draft);
    /// <summary>A save stopped part-way: tells the user and draws what is in the game now.</summary>
    void SaveFailed(ClubEntry club, Exception error);
    ulong TeamOf(ClubEntry club);
    bool IsValid(ulong clubAddress);
}

/// <summary>Clubs: the list with balance and reputation, and a tab per club with sections down its left.</summary>
internal class ClubScreen(IScreenHost host, IClubPanels panels)
    : EntityScreen<ClubEntry, MainWindow.ClubPickerItem, ClubDraft>(host, "club", "Clubs", "clubs", "Club or ID")
{
    IReadOnlyList<PlayerRecord> playersAtLoad = [];
    // Metadata travels with the row result: an old worker cannot overwrite the next connection's publication.
    internal sealed class LoadedRows(IReadOnlyList<ClubEntry> clubs, IReadOnlyList<PlayerRecord>? fresh, DateOnly? date)
        : List<MainWindow.ClubPickerItem>(clubs.Count)
    {
        public IReadOnlyList<ClubEntry> Clubs { get; } = clubs;
        public IReadOnlyList<PlayerRecord>? Fresh { get; } = fresh;
        public DateOnly? Date { get; } = date;
    }
    static readonly string[] GenderFilterKeys = ["Men", "Women", "All"];
    readonly ComboBox genderFilter = new()
    {
        ItemsSource = GenderFilterKeys,
        ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<string>((s, _) => new TextBlock { Text = s is null ? "" : Loc.S(s) }),
        SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 260,
    };
    internal ComboBox GenderFilter => genderFilter;

    Fm27Editor.Core.Offsets.Gender? SelectedGender() => genderFilter.SelectedIndex switch
    {
        0 => Fm27Editor.Core.Offsets.Gender.Men,
        1 => Fm27Editor.Core.Offsets.Gender.Women,
        _ => null,
    };

    protected override Control BuildSearchHeader(TextBox searchBox)
    {
        Loc.BindTip(genderFilter, "Gender filter");
        genderFilter.SelectionChanged += (_, _) => Filter();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,104"), ColumnSpacing = 6 };
        Grid.SetColumn(searchBox, 0); row.Children.Add(searchBox);
        Grid.SetColumn(genderFilter, 1); row.Children.Add(genderFilter);
        return row;
    }

    internal void SetLoadedRowsForTest(IReadOnlyList<MainWindow.ClubPickerItem> rows)
    {
        Rows = rows;
        Clubs = rows.Select(r => r.Club).ToList();
        Filter(reportStatus: true);
    }

    protected override string FilteredMessage(int shown, int total) =>
        SelectedGender() is { } g && SearchText.Length == 0
            ? Loc.F("{0:N0} of {1:N0} clubs shown ({2:N0} hidden by {3} filter)", shown, total, total - shown, Loc.S(g.ToString()))
            : base.FilteredMessage(shown, total);

    /// <summary>Set from the command line (--club id): the club to show in the list once it has loaded.</summary>
    public int? StartupClubId { get; set; }

    /// <summary>Every club of the last load, before the search narrowed the list.</summary>
    public IReadOnlyList<ClubEntry> Clubs { get; private set; } = [];

    public StackPanel EditorPanel => Editor;
    public Dictionary<ulong, ClubDraft> DraftStore => Drafts;
    public void ClearDrafts() => Drafts.Clear();

    protected override bool HasRail => true;

    protected override void ConfigureColumns(DataGrid grid)
    {
        var id = new DataGridTextColumn { Header = Loc.S("ID"), Binding = new Binding(nameof(MainWindow.ClubPickerItem.Id)), Width = new DataGridLength(110), SortMemberPath = nameof(MainWindow.ClubPickerItem.Id) };
        id.CellStyleClasses.Add("numeric");
        grid.Columns.Add(id);
        grid.Columns.Add(new DataGridTextColumn { Header = Loc.S("Name"), Binding = new Binding(nameof(MainWindow.ClubPickerItem.Name)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = nameof(MainWindow.ClubPickerItem.Name) });
        var balance = new DataGridTextColumn { Header = Loc.S("Balance"), Binding = new Binding(nameof(MainWindow.ClubPickerItem.Balance)) { StringFormat = "N0" }, Width = new DataGridLength(173), SortMemberPath = nameof(MainWindow.ClubPickerItem.Balance) };
        balance.CellStyleClasses.Add("numeric");
        grid.Columns.Add(balance);
        var reputation = new DataGridTextColumn { Header = Loc.S("Reputation"), Binding = new Binding(nameof(MainWindow.ClubPickerItem.Reputation)), Width = new DataGridLength(126), SortMemberPath = nameof(MainWindow.ClubPickerItem.Reputation) };
        reputation.CellStyleClasses.Add("numeric");
        grid.Columns.Add(reputation);
        panels.SectionRail.Margin = default;
        RailHost.Content = panels.SectionRail;
    }

    protected override void BeforeLoad()
    {
        playersAtLoad = panels.ShownPlayers;
    }

    protected override IReadOnlyList<MainWindow.ClubPickerItem> LoadRows(GameSession session, LoadedGameData? data)
    {
        var date = session.ReadGameDate();
        IReadOnlyList<PlayerRecord>? fresh = null;
        IReadOnlyList<ClubEntry> clubs;
        if (data is not null) clubs = data.Clubs;
        else
        {
            IReadOnlyList<PlayerRecord> source = playersAtLoad;
            if (source.Count == 0)
            {
                var all = session.Players.LoadAll();
                foreach (var p in all) p.GameDate = date;
                fresh = all;
                source = all;
            }
            clubs = session.LoadClubs(source.ToArray());
        }
        var filtered = ClubCatalog.Filter(clubs, null).ToList();
        using var reads = new BulkReadScope(session.Memory);
        if (filtered.Count > 0)
        {
            var clubAddrs = new ulong[filtered.Count];
            var financeAddrs = new List<ulong>(filtered.Count);
            var teamAddrs = new List<ulong>(filtered.Count);
            for (int i = 0; i < filtered.Count; i++)
            {
                clubAddrs[i] = filtered[i].Address;
                if (filtered[i].Finance != 0) financeAddrs.Add(filtered[i].Finance);
                if (filtered[i].Teams.Count > 0) teamAddrs.Add(filtered[i].Teams[0].Address);
            }
            reads.Prefetch(clubAddrs, 0xD0);
            if (financeAddrs.Count > 0) reads.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(financeAddrs), 0x100);
            if (teamAddrs.Count > 0) reads.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(teamAddrs), 0x80);
        }
        var access = new ClubFieldAccess(reads.Memory, session.Profile, session.ModuleBase);
        var items = new LoadedRows(clubs, fresh, date);
        foreach (var club in filtered)
        {
            var item = new MainWindow.ClubPickerItem(club);
            try
            {
                item.Balance = access.Read(club.Address, ClubField.Balance);
                item.Reputation = club.Teams.Count > 0 ? access.Read(club.Teams[0].Address, ClubField.TeamReputation) : null;
            }
            catch (Exception) { }
            items.Add(item);
        }
        return items;
    }

    protected override void RowsLoaded()
    {
        if (Rows is LoadedRows done)
        {
            panels.ClubsLoaded(done.Clubs, done.Fresh, done.Date);
            Clubs = Rows.Select(r => r.Club).ToList();
        }
        if (StartupClubId is { } id)
        {
            StartupClubId = null;
            Dispatcher.UIThread.Post(() => Reveal(r => r.Id == id));
        }
    }

    protected override void OnReconnected()
    {
        Clubs = [];
        playersAtLoad = [];
        genderFilter.SelectedIndex = 0;
        panels.Reconnected();
    }

    protected override IReadOnlyList<MainWindow.ClubPickerItem> Apply(IReadOnlyList<MainWindow.ClubPickerItem> rows, string foldedQuery)
    {
        var gender = SelectedGender();
        if (foldedQuery.Length == 0 && gender is null) return rows;
        var keep = ClubCatalog.Filter(rows.Select(r => r.Club).ToList(), foldedQuery, gender).ToHashSet();
        return rows.Where(r => keep.Contains(r.Club)).ToList();
    }

    protected override bool Matches(MainWindow.ClubPickerItem row, string q) => ClubCatalog.Filter([row.Club], q, SelectedGender()).Count > 0;

    protected override void OnRefreshLanguage()
    {
        var idx = genderFilter.SelectedIndex;
        genderFilter.ItemsSource = null;
        genderFilter.ItemsSource = GenderFilterKeys;
        genderFilter.SelectedIndex = idx;
    }

    protected override ClubEntry RecordOf(MainWindow.ClubPickerItem row) => row.Club;
    protected override ulong AddressOf(ClubEntry record) => record.Address;
    protected override EntityTabIdentity IdentityOf(ClubEntry record) => EntityTabIdentity.Database(record.Id);
    protected override string NameOf(ClubEntry record) => record.DisplayName;
    protected override ClubDraft NewDraft() => throw new InvalidOperationException("A club draft needs its club");
    protected override ClubDraft NewDraftFor(ClubEntry record) => new(record.Address);
    protected override bool HasChanges(ClubEntry record, ClubDraft draft) => draft.InvalidCount == 0 && (draft.CanSave || panels.HasPending(record));

    protected override ClubEntry? Reload(ClubEntry record) => panels.IsValid(record.Address) ? record : null;

    protected override MainWindow.ClubPickerItem Refresh(MainWindow.ClubPickerItem row, ClubEntry fresh)
    {
        var item = new MainWindow.ClubPickerItem(fresh) { Balance = row.Balance, Reputation = row.Reputation };
        try
        {
            var access = Host.Backend.Session.ClubFields;
            item.Balance = access.Read(fresh.Address, ClubField.Balance);
            item.Reputation = fresh.Teams.Count > 0 ? access.Read(fresh.Teams[0].Address, ClubField.TeamReputation) : null;
        }
        catch (Exception) { }
        return item;
    }

    protected override Func<ulong, int, ChangeLabel?>? LabelsOf(ClubEntry club)
    {
        var session = Host.Backend.Session;
        return EntityChangeLabels.ForClub(session.Memory, session.Profile, session.ModuleBase, club.Address, panels.TeamOf(club));
    }

    protected override void BuildEditor(ClubEntry club, ClubDraft draft, Action changed)
    {
        // A reconnect empties the rail host, so the section list is put back every time a club is drawn.
        if (!ReferenceEquals(RailHost.Content, panels.SectionRail)) RailHost.Content = panels.SectionRail;
        panels.BuildClub(club, draft);
    }

    protected override string? Validate(ClubEntry club, ClubDraft draft, out Func<int> write)
    {
        write = () => panels.SaveClub(club, draft);
        return draft.InvalidCount > 0 ? Loc.F("{0} invalid value(s)", draft.InvalidCount) : null;
    }

    protected override bool OnSaveFailed(ClubEntry club, Exception error)
    {
        panels.SaveFailed(club, error);
        return true;
    }

    /// <summary>The header and body of the open club, for the window's section code to fill.</summary>
    public ContentControl Header => HeaderHost;
}
