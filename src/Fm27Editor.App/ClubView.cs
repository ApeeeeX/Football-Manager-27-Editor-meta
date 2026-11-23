using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Game;
using Fm27Editor.Core.Nations;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public sealed partial class MainWindow : IClubPanels
{
    public sealed record ClubSectionOption(ClubSection Section, string Title)
    {
        public override string ToString() => Loc.S(Title);
    }

    public sealed class ClubPickerItem(ClubEntry club)
    {
        public ClubEntry Club { get; } = club;
        public int? Id => Club.Id;
        public string Name => Club.Name ?? Club.DisplayName;
        public long? Balance { get; set; }
        public long? Reputation { get; set; }
        public override string ToString() => $"{Club.DisplayName}, {Id}";
    }

    readonly ListBox clubSectionList = SectionHost.CreateRailListBox(stretchVertically: false);
    readonly HashSet<ClubSection> clubDirtySections = [];
    readonly HashSet<ClubSection> clubScalarSections = [];
    int clubChangeCount;
    readonly Dictionary<ulong, ulong> clubSelectedTeam = [];
    Dictionary<int, string>? visionCompetitionNames, visionNationNames, visionContinentNames;
    readonly Dictionary<(ulong Club, ClubSection Section), int> clubListSelectedRow = [];

    sealed class ClubVisionRowDraft
    {
        public Dictionary<string, string> Inputs { get; } = [];
        public HashSet<string> Invalid { get; } = [];
        public int? Importance { get; set; }
        public byte? TypeCode { get; set; }
        public int? SourceType { get; set; }
        public (DateOnly Start, DateOnly End)? Dates { get; set; }
        public (ClubVisionItemKind Kind, ushort Index)? ItemTarget { get; set; }
        public string? ItemTargetName { get; set; }
        public int? Target { get; set; }
        public bool HasChanges => Importance is not null || TypeCode is not null || SourceType is not null || Dates is not null || ItemTarget is not null || Target is not null;
    }
    readonly Dictionary<ulong, Dictionary<ulong, ClubVisionRowDraft>> clubVisionDrafts = [];
    sealed record ClubVisionTargetChoice(string Name, ushort Index);

    sealed class ClubAffiliationRowDraft
    {
        public bool? ScoutingShared { get; set; }
        public bool? PlayersOnLoan { get; set; }
        public bool? FirstOption { get; set; }
        public bool? PlayFriendlies { get; set; }
        int? parentWagePercent;
        int? friendlyChance;
        // FMRTE documents both as 0-100; the box may show a typed value, the stored edit is always in range.
        public int? ParentWagePercent { get => parentWagePercent; set => parentWagePercent = value is { } v ? Math.Clamp(v, 0, 100) : null; }
        public int? FriendlyChance { get => friendlyChance; set => friendlyChance = value is { } v ? Math.Clamp(v, 0, 100) : null; }
        public DateOnly? StartDate { get; set; }
        public DateOnly? EndDate { get; set; }
        int? annualFee;
        public int? AnnualFee { get => annualFee; set => annualFee = value is { } v ? Math.Max(0, v) : null; }

        public bool HasChanges => ScoutingShared != null || PlayersOnLoan != null || FirstOption != null
            || PlayFriendlies != null || ParentWagePercent != null || FriendlyChance != null
            || StartDate != null || EndDate != null || AnnualFee != null;
    }
    readonly Dictionary<ulong, Dictionary<ulong, ClubAffiliationRowDraft>> clubAffiliationDrafts = [];

    sealed class ClubAllowanceDraft
    {
        public StaffAllowanceSnapshot? Snapshot { get; set; }
        public Dictionary<StaffAllowanceField, int> Pending { get; } = [];
        public HashSet<StaffAllowanceField> Invalid { get; } = [];
        public bool HasChanges => Pending.Count > 0 || Invalid.Count > 0;
    }
    readonly Dictionary<ulong, ClubAllowanceDraft> clubAllowanceDrafts = [];

    ClubScreen clubScreen = null!;
    ClubEntry? selectedClub => clubScreen.Selected;
    StackPanel clubEditorPanel => clubScreen.EditorPanel;
    Dictionary<ulong, ClubDraft> clubDrafts => clubScreen.DraftStore;
    IReadOnlyList<ClubEntry> loadedClubs => clubScreen.Clubs;
    bool clubsActive => clubScreen.IsActive;
    ClubSection selectedClubSection = ClubSection.Information;
    ClubSection selectedFinanceSubSection = ClubSection.Finances;
    bool clubUpdating;
    int clubRenderGeneration;
    int clubSaveTotal;
    bool clubSaveInScalar;

    /// <summary>Draws the open club again (after something in it changed).</summary>
    void RenderClubEditor() => clubScreen.Reopen();

    void PickClub(ClubEntry club) => clubScreen.Pick(club);

    Task LoadClubsScreenAsync(bool forceReloadPlayers)
    {
        if (forceReloadPlayers) { clubCache = null; rows = []; }
        return clubScreen.LoadAsync();
    }

    void RegisterClubs()
    {
        ParseClubStartupArgs(out bool startOnClubs, out int? startupClubId);
        BuildClubSectionRail();
        clubScreen = AddScreen(host => new ClubScreen(host, this) { StartupClubId = startupClubId });
        if (startOnClubs) Opened += (_, _) => Dispatcher.UIThread.Post(() => SwitchModule("Clubs"));
        timer.Tick += async (_, _) => await CheckSelectedClubAsync();
    }

    static void ParseClubStartupArgs(out bool startOnClubs, out int? clubId)
    {
        startOnClubs = false;
        clubId = null;
        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--clubs", StringComparison.OrdinalIgnoreCase))
            {
                startOnClubs = true;
            }
            else if (string.Equals(args[i], "--club", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                     && int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer, Inv, out int id))
            {
                startOnClubs = true;
                clubId = id;
                i++;
            }
        }
    }

    void BuildClubSectionRail()
    {
        clubSectionList.ItemTemplate = new FuncDataTemplate<ClubSectionOption>((opt, _) =>
            opt is null
                ? null
                : new TextBlock
                {
                    Text = clubDirtySections.Contains(opt.Section) ? Loc.S(opt.Title) + " *" : Loc.S(opt.Title),
                    FontSize = 13,
                    FontWeight = FontWeight.Medium,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 2, 8, 2),
                });
        RefreshClubSectionRail();
        clubSectionList.SelectionChanged += (_, _) =>
        {
            if (clubUpdating) return;
            if (clubSectionList.SelectedItem is ClubSectionOption opt && opt.Section != selectedClubSection)
            {
                selectedClubSection = opt.Section;
                RenderClubEditor();
                UpdateClubStatusFromDraft();
            }
        };
    }

    // What the club screen asks of the window.
    IReadOnlyList<ClubSection> VisibleClubSections()
    {
        bool connected = backend.State.Status == ConnectionStatus.Connected;
        IReadOnlyList<ClubSection> sections = connected && backend.Session.Profile.YouthCandidates is null
            ? ClubFieldInfo.Sections.Where(s => s != ClubSection.YouthIntake).ToArray()
            : ClubFieldInfo.Sections;
        return connected && backend.Session.Profile.TeamTactics is not null
            ? [.. sections.Where(s => s != ClubSection.Supporters), ClubSection.TacticalUnderstanding, ClubSection.Supporters]
            : sections;
    }

    void RefreshClubSectionRail()
    {
        var visible = VisibleClubSections();
        if (!visible.Contains(selectedClubSection)) selectedClubSection = visible[0];
        clubUpdating = true;
        clubSectionList.ItemsSource = visible
            .Select(s => new ClubSectionOption(s, ClubFieldInfo.SectionTitle(s)))
            .ToArray();
        int railIndex = 0;
        for (int i = 0; i < visible.Count; i++) if (visible[i] == selectedClubSection) { railIndex = i; break; }
        clubSectionList.SelectedIndex = railIndex;
        clubUpdating = false;
    }

    ListBox IClubPanels.SectionRail => clubSectionList;
    IReadOnlyList<ClubEntry>? IClubPanels.CachedClubs => clubCache;
    IReadOnlyList<PlayerRecord> IClubPanels.ShownPlayers => rows.OfType<PlayerRecord>().ToArray();

    void IClubPanels.ClubsLoaded(IReadOnlyList<ClubEntry> clubs, IReadOnlyList<PlayerRecord>? freshPlayers, DateOnly? gameDate)
    {
        if (freshPlayers is not null && rows.Count == 0)
            rows = freshPlayers.OrderByDescending(p => p.CurrentAbility).Cast<object>().ToArray();
        clubCache = clubs;
        if (gameDate is { } d)
        {
            gameDateText.Text = d.ToString("dd MMM yyyy", Inv);
            gameDateBadge.IsVisible = true;
        }
        else gameDateBadge.IsVisible = false;
        UpdateConnectionBadge();
        if (clubsActive) connection.Text = $"{backend.State.Message} · {clubs.Count:N0}";
    }

    void IClubPanels.Reconnected()
    {
        youthIntakeDrafts.Clear();
        selectedKitTab.Clear();
        kitDrafts.Clear();
        clubFamiliarityDrafts.Clear();
        pendingAffiliationRemovals.Clear();
        clubSelectedTeam.Clear();
        visionCompetitionNames = visionNationNames = visionContinentNames = null;
        clubListSelectedRow.Clear();
        clubVisionDrafts.Clear();
        clubAffiliationDrafts.Clear();
        clubAllowanceDrafts.Clear();
        if (!clubsActive) return;
        if (backend.State.Status == ConnectionStatus.Connected) { rows = []; clubCache = null; competitionCache = null; }
        else gameDateBadge.IsVisible = false;
    }

    bool IClubPanels.HasPending(ClubEntry club) =>
        (kitDrafts.TryGetValue(club.Address, out var kits) && kits.Count > 0)
        || pendingAffiliationRemovals.ContainsKey(club.Address) || clubFamiliarityDrafts.ContainsKey(club.Address)
        || (youthIntakeDrafts.TryGetValue(club.Address, out var youth) && youth.HasChanges)
        || (clubAllowanceDrafts.TryGetValue(club.Address, out var allow) && allow.HasChanges);

    ulong IClubPanels.TeamOf(ClubEntry club) => ResolveSelectedTeam(club)?.Address ?? 0;
    bool IClubPanels.IsValid(ulong clubAddress) => IsClubObjectValid(clubAddress);

    int IClubPanels.SaveClub(ClubEntry club, ClubDraft draft)
    {
        int count = 0;
        // FMRTE docs: the six supporter groups must add up to exactly 100% (an empty profile may be all zero).
        ClubField[] supporterFields = [ClubField.SupportersHardcore, ClubField.SupportersCore, ClubField.SupportersFamily,
            ClubField.SupportersFairWeather, ClubField.SupportersCorporate, ClubField.SupportersCasual];
        long supporterTotal = 0;
        bool supporterEdited = false;
        foreach (var field in supporterFields)
        {
            if (draft.Current(field) is not { } value) continue;
            supporterTotal += value;
            supporterEdited |= draft.IsChanged(field);
        }
        if (supporterEdited && supporterTotal is not (0 or 100))
        {
            status.Text = Loc.F("Supporter groups must add up to 100% (currently {0}%).", supporterTotal);
            return 0;
        }
        clubSaveInScalar = false;
        if (kitDrafts.TryGetValue(club.Address, out var pending) && pending.Count > 0)
        {
            count += pending.Count;
            SaveKitDrafts(backend.Session, club);
        }
        if (clubFamiliarityDrafts.TryGetValue(club.Address, out var familiarityText))
        {
            if (!int.TryParse(familiarityText, out int familiarityValue) || familiarityValue is < 0 or > 100)
                throw new InvalidOperationException(Loc.S("Tactical familiarity must be a whole number from 0 to 100"));
            var session = backend.Session;
            var access = session.PlayerFamiliarity;
            foreach (var team in club.Teams)
                foreach (var squadPlayer in session.ReadSquad(team.Address))
                    if (access.Read(squadPlayer.Address) is { } snapshot)
                        access.SetTactical(snapshot, familiarityValue);
            clubFamiliarityDrafts.Remove(club.Address);
            count++;
        }
        if (pendingAffiliationRemovals.TryGetValue(club.Address, out var affiliation))
        {
            var session = backend.Session;
            new ClubAffiliationWriter(session.Memory, session.Profile, session.Game.ModuleBase).Remove(club.Address, affiliation);
            pendingAffiliationRemovals.Remove(club.Address);
            count++;
        }
        if (youthIntakeDrafts.TryGetValue(club.Address, out var youth) && (youth.Count > 0 || youth.InvalidCount > 0))
            count += SaveYouthIntake(backend.Session, club);
        if (clubAllowanceDrafts.TryGetValue(club.Address, out var allow) && allow.HasChanges)
            count += SaveClubAllowances(backend.Session, club, allow);
        if (draft.CanSave)
        {
            clubSaveInScalar = true;
            clubSaveTotal = draft.Count;
            count += draft.Save(backend.Edits, backend.Session.ClubFields, target => backend.AcceptFrozenClubEdit(club, target.Field));
        }
        return count;
    }

    void IClubPanels.SaveFailed(ClubEntry club, Exception error)
    {
        if (!clubSaveInScalar || !clubDrafts.TryGetValue(club.Address, out var draft))
        {
            // A write can fail after earlier writes succeeded; discard stale drafts and reload.
            kitDrafts.Remove(club.Address);
            pendingAffiliationRemovals.Remove(club.Address);
            clubAllowanceDrafts.Remove(club.Address);
            RenderClubEditor();
            status.Text = Loc.F("Some changes may have been saved. {0}", UserMessage.For(error));
            return;
        }
        ulong teamAddr = ResolveSelectedTeam(club)?.Address ?? 0;
        var access = backend.Session.ClubFields;
        int saved = clubSaveTotal - draft.Count;
        try
        {
            var fresh = access.ReadAll(club.Address, teamAddr);
            if (fresh.Count == 0 && !IsClubObjectValid(club.Address))
            {
                clubDrafts.Remove(club.Address);
                status.Text = Loc.S("Club no longer available; reloading");
                _ = LoadClubsScreenAsync(forceReloadPlayers: true);
                return;
            }
            draft.Rebase(club.Address, teamAddr, backend.Session.Profile, fresh);
            RenderClubEditor();
            status.Text = saved > 0
                ? Loc.F("{0} saved. {1} Review remaining changes.", saved, ClubUserMessage(error))
                : ClubUserMessage(error);
        }
        catch (Exception reloadError)
        {
            status.Text = Loc.F("{0} saved. {1}", saved, ClubUserMessage(reloadError));
        }
    }

    async Task CheckSelectedClubAsync()
    {
        if (!clubsActive || selectedClub is not { } club || busy || backend.State.Status != ConnectionStatus.Connected)
            return;
        try
        {
            ulong addr = club.Address;
            bool ok = await Task.Run(() => IsClubObjectValid(addr));
            if (!ok && clubsActive && selectedClub?.Address == addr)
            {
                clubDrafts.Clear();
                status.Text = Loc.S("Club no longer available; reloading");
                await LoadClubsScreenAsync(forceReloadPlayers: true);
            }
        }
        catch (Exception ex)
        {
            status.Text = ClubUserMessage(ex);
        }
    }

    bool IsClubObjectValid(ulong clubAddress)
    {
        if (clubAddress == 0 || backend.Session.Profile.Club is not { } clubOffsets) return false;
        Span<byte> vt = stackalloc byte[8];
        return backend.Session.Memory.TryRead(clubAddress, vt)
               && BitConverter.ToUInt64(vt) == backend.Session.ModuleBase + clubOffsets.ClubVtableRva;
    }

    TeamEntry? ResolveSelectedTeam(ClubEntry club)
    {
        var ordered = ClubCatalog.OrderedTeams(club);
        if (clubSelectedTeam.TryGetValue(club.Address, out ulong savedAddr)
            && ordered.FirstOrDefault(t => t.Address == savedAddr) is { } match)
        {
            return match;
        }
        return ClubCatalog.DefaultTeam(club);
    }

    void UpdateClubButtonsAndStatus(ClubDraft draft)
    {
        if (selectedClub is { } club) UpdateKitSaveButtons(club.Address);
        UpdateClubStatusFromDraft();
    }

    /// <summary>
    /// Stars the rail entries whose own drafts hold edits (FMRTE marks every child tab that changed) and keeps the
    /// shell's tab star in step. Each section keeps its own store, so a Club Vision or Kits edit stars that tab only.
    /// </summary>
    void UpdateClubStatusFromDraft()
    {
        if (!clubsActive) return;
        if (selectedClub is not { } club || !clubDrafts.TryGetValue(club.Address, out var draft))
        {
            status.Text = "";
            clubDirtySections.Clear();
            clubScalarSections.Clear();
            clubChangeCount = 0;
            SetActiveTabDirty(false);
            return;
        }

        int scalarChanges = draft.Count;
        int youthChanges = youthIntakeDrafts.TryGetValue(club.Address, out var youth) ? youth.Count : 0;
        int youthInvalid = youth?.InvalidCount ?? 0;
        int visionChanges = club.Teams.Sum(t => clubVisionDrafts.TryGetValue(t.Address, out var vision) ? vision.Values.Count(r => r.HasChanges) : 0);
        int affiliationChanges = (clubAffiliationDrafts.TryGetValue(club.Address, out var affiliations) ? affiliations.Values.Count(r => r.HasChanges) : 0)
            + (pendingAffiliationRemovals.ContainsKey(club.Address) ? 1 : 0);
        int kitChanges = kitDrafts.TryGetValue(club.Address, out var kits) ? kits.Values.Count(k => k.Colours.Count > 0 || k.Style is not null) : 0;
        int allowanceChanges = clubAllowanceDrafts.TryGetValue(club.Address, out var allow) ? allow.Pending.Count : 0;
        int allowanceInvalid = allow?.Invalid.Count ?? 0;
        int total = scalarChanges + youthChanges + visionChanges + affiliationChanges + kitChanges + allowanceChanges;
        int invalidTotal = draft.InvalidCount + youthInvalid + allowanceInvalid;

        if (invalidTotal > 0) status.Text = Loc.F("{0} invalid value(s)", invalidTotal);
        else if (total > 0) status.Text = Loc.F("{0} unsaved change(s)", total);
        else status.Text = "";

        if (scalarChanges == 0) clubScalarSections.Clear();
        else if (scalarChanges > clubChangeCount && ClubFieldInfo.ScalarSections.Contains(selectedClubSection)) clubScalarSections.Add(selectedClubSection);
        clubChangeCount = scalarChanges;

        var dirty = new HashSet<ClubSection>(clubScalarSections);
        if (youthChanges > 0 || youthInvalid > 0) dirty.Add(ClubSection.YouthIntake);
        if (visionChanges > 0) dirty.Add(ClubSection.ClubVision);
        if (affiliationChanges > 0) dirty.Add(ClubSection.Affiliations);
        if (kitChanges > 0) dirty.Add(ClubSection.Kits);
        if (clubFamiliarityDrafts.ContainsKey(club.Address)) dirty.Add(ClubSection.Relationships);
        if (allowanceChanges > 0 || allowanceInvalid > 0) dirty.Add(ClubSection.Information);

        if (!dirty.SetEquals(clubDirtySections))
        {
            clubDirtySections.Clear();
            foreach (var section in dirty) clubDirtySections.Add(section);
            RefreshClubSectionRail();
        }

        SetActiveTabDirty(total > 0 || invalidTotal > 0);
    }

    void IClubPanels.BuildClub(ClubEntry club, ClubDraft draft)
    {
        clubRenderGeneration++;
        if (backend.State.Status != ConnectionStatus.Connected) return;

        var session = backend.Session;
        var profile = session.Profile;
        var access = session.ClubFields;
        var orderedTeams = ClubCatalog.OrderedTeams(club);
        var activeTeam = ResolveSelectedTeam(club);
        backend.Edits.RegisterLabels("Club", club.DisplayName,
            Fm27Editor.Core.History.EntityChangeLabels.ForClub(session.Memory, profile, session.ModuleBase, club.Address, activeTeam?.Address ?? 0));
        ulong teamAddr = activeTeam?.Address ?? 0;

        var values = access.ReadAll(club.Address, teamAddr);
        draft.AbsorbSnapshot(club.Address, teamAddr, profile, values);
        UpdateKitSaveButtons(club.Address);

        NationRecord? clubNation = null;
        if (profile.Club?.Nation is { } nationOffset)
        {
            Span<byte> nationPtrBuf = stackalloc byte[8];
            if (session.Memory.TryRead(club.Address + (ulong)nationOffset, nationPtrBuf))
            {
                ulong nationAddr = BitConverter.ToUInt64(nationPtrBuf);
                if (nationAddr != 0)
                {
                    nationCatalogue ??= (backend.Data?.Nations ?? session.Nations.LoadAll()).Where(n => n.DisplayName.Length > 0).OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                    clubNation = nationCatalogue.FirstOrDefault(n => n.Address == nationAddr);
                }
            }
        }

        string? clubDivisionName = null;
        ulong clubDivisionAddr = 0;
        var playerOfClub = rows.OfType<PlayerRecord>().FirstOrDefault(p => p.Club?.Address == club.Address && p.Division is not null);
        if (playerOfClub?.Division is { } div && !string.IsNullOrEmpty(div.DisplayName))
        {
            clubDivisionName = div.DisplayName;
            clubDivisionAddr = div.Address;
        }
        else if (profile.Competition is { } compOffsets)
        {
            ulong teamAddress = club.FirstTeam?.Address ?? (orderedTeams.Count > 0 ? orderedTeams[0].Address : 0);
            if (teamAddress != 0)
            {
                Span<byte> ptrBuf = stackalloc byte[8];
                if (session.Memory.TryRead(teamAddress + (ulong)compOffsets.TeamCompetition, ptrBuf))
                {
                    ulong compAddr = BitConverter.ToUInt64(ptrBuf);
                    if (compAddr != 0)
                    {
                        try
                        {
                            if (session.Competitions.Reload(compAddr) is { } c)
                            {
                                clubDivisionName = c.DisplayName;
                                clubDivisionAddr = c.Address;
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        var badges = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (club.Id is { } id)
            badges.Children.Add(UiKit.MetaText($"ID {id.ToString(Inv)}"));

        if (!string.IsNullOrWhiteSpace(club.ShortName)
            && !string.Equals(club.ShortName, club.Name, StringComparison.Ordinal))
        {
            if (badges.Children.Count > 0) badges.Children.Add(UiKit.MetaDot());
            badges.Children.Add(UiKit.MetaText(club.ShortName, highlight: true));
        }

        if (clubNation is not null)
        {
            if (badges.Children.Count > 0) badges.Children.Add(UiKit.MetaDot());
            var flag = graphics.NationFlag(clubNation.Id, 14);
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { flag, new TextBlock { Text = clubNation.DisplayName, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center } },
            };
            var nationBtn = UiKit.MetaLinkButton(content);
            nationBtn.Click += async (_, _) => await OpenPlayerNationAsync(clubNation.Address);
            badges.Children.Add(nationBtn);
        }

        if (!string.IsNullOrEmpty(clubDivisionName))
        {
            if (badges.Children.Count > 0) badges.Children.Add(UiKit.MetaDot());
            var divBtn = UiKit.MetaLinkButton(clubDivisionName);
            if (clubDivisionAddr != 0)
                divBtn.Click += (_, _) => OpenPlayerCompetition(clubDivisionAddr);
            badges.Children.Add(divBtn);
        }

        if (orderedTeams.Count > 0)
        {
            if (badges.Children.Count > 0) badges.Children.Add(UiKit.MetaDot());
            badges.Children.Add(UiKit.MetaText(Loc.F("{0} players", orderedTeams[0].SquadCount)));
        }

        if (club.Gender == Fm27Editor.Core.Offsets.Gender.Women)
        {
            if (badges.Children.Count > 0) badges.Children.Add(UiKit.MetaDot());
            badges.Children.Add(UiKit.MetaText(Loc.S("Women"), highlight: true));
        }

        var crestImg = graphics.Image("club", club.Id, 72);
        clubScreen.Header.Content = UiKit.EntityBanner(club.Name ?? club.DisplayName, badges, null, crestImg);

        if (HookClubSquadsView(club, orderedTeams, activeTeam, teamAddr)) return;

        if (!VisibleClubSections().Contains(selectedClubSection))
        {
            selectedClubSection = ClubSection.Information;
            RefreshClubSectionRail();
        }

        if (selectedClubSection == ClubSection.YouthIntake)
        {
            RenderClubYouthIntake(session, club);
            return;
        }

        if (selectedClubSection == ClubSection.Finances)
        {
            RenderClubFinancesSection(session, club, orderedTeams, activeTeam, teamAddr, profile, access, values, draft);
            return;
        }

        if (ClubFieldInfo.IsListSection(selectedClubSection))
        {
            RenderClubListSection(selectedClubSection, session, club, orderedTeams, activeTeam, teamAddr);
            return;
        }

        RenderClubScalarSection(selectedClubSection, club, orderedTeams, teamAddr, profile, access, values, draft);
    }

    void RenderClubScalarSection(
        ClubSection section,
        ClubEntry club,
        IReadOnlyList<TeamEntry> orderedTeams,
        ulong teamAddr,
        Fm27Editor.Core.Offsets.OffsetProfile profile,
        ClubFieldAccess access,
        IReadOnlyDictionary<ClubField, long> values,
        ClubDraft draft)
    {
        if (section == ClubSection.TacticalUnderstanding && profile.TeamTactics is not null)
        {
            if (orderedTeams.Count > 1) clubEditorPanel.Children.Add(BuildClubTeamSelector(club, orderedTeams, teamAddr));
            clubEditorPanel.Children.Add(BuildTeamTacticsCard(backend.Session, club, teamAddr)
                ?? UiKit.Card("Tactical Understanding", new TextBlock { Text = Loc.S("This team's tactics are not available"), Margin = new Thickness(12, 8), Foreground = UiKit.TextSecondary }));
            clubEditorPanel.Children.Add(BuildTeamCohesionCard(session: backend.Session, club, orderedTeams.FirstOrDefault(t => t.Address == teamAddr)));
            return;
        }

        var groups = ClubSectionLayout.BuildGroups(section, profile, access, club.Address, teamAddr, values);
        if (groups.Count == 0)
            return;

        if (section == ClubSection.TacticalUnderstanding)
        {
            if (orderedTeams.Count > 1) clubEditorPanel.Children.Add(BuildClubTeamSelector(club, orderedTeams, teamAddr));
            // FMRTE: the Tactical Understanding card has a Max button that sets every bar to 100; Team Cohesion has its own.
            var max = new Button { Content = Loc.S("Max"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left };
            max.Click += (_, _) =>
            {
                foreach (var g in groups) foreach (var f in g.Fields) draft.Stage(f, 100);
                RenderClubEditor();
                UpdateClubStatusFromDraft();
            };
            clubEditorPanel.Children.Add(max);
            foreach (var g in groups) clubEditorPanel.Children.Add(BuildClubGroupCard(g, draft));
            clubEditorPanel.Children.Add(BuildTeamCohesionCard(session: backend.Session, club, orderedTeams.FirstOrDefault(t => t.Address == teamAddr)));
            return;
        }

        if (section == ClubSection.Information)
        {
            var byTitle = groups.ToDictionary(g => g.Title, StringComparer.Ordinal);
            var ordered = new List<(Control Card, int Weight)>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            void AddGroup(string title)
            {
                if (byTitle.TryGetValue(title, out var group) && used.Add(title))
                    ordered.Add((BuildClubGroupCard(group, draft), group.Fields.Count));
            }

            AddGroup("Overview");
            if (backend.Session.StaffAllowances.IsSupported)
            {
                StaffAllowanceSnapshot? allowanceSnapshot = null;
                try { allowanceSnapshot = backend.Session.StaffAllowances.ReadForClub(club.Address); }
                catch { allowanceSnapshot = null; }
                ordered.Add((BuildClubStaffAllowancesCard(club, allowanceSnapshot), 7));
            }
            ordered.Add((BuildClubBoardRequestsCard(backend.Session), 3));
            if (new ClubProjects(backend.Session.Memory, backend.Session.Profile, backend.Session.ModuleBase).IsSupported)
                ordered.Add((BuildClubProjectsCard(backend.Session, club), 2));
            if (backend.Session.Profile.Competition?.Fixtures is not null && club.FirstTeam is not null)
                ordered.Add((BuildClubSwapPreviewCard(backend.Session, club), 2));
            AddGroup("Facilities");
            AddGroup("Teams");
            foreach (var g in groups)
                AddGroup(g.Title);

            clubEditorPanel.Children.Add(UiKit.TwoColumnMasonry(ordered, gap: 14));
            return;
        }

        var items = groups.Select(g => (BuildClubGroupCard(g, draft), g.Fields.Count)).ToList();
        clubEditorPanel.Children.Add(UiKit.TwoColumnMasonry(items, gap: 14));
    }

    /// <summary>The team's Tactical Understanding bars, each dragged or typed for the whole squad; null when its tactics are unknown.</summary>
    Control? BuildTeamTacticsCard(GameSession session, ClubEntry club, ulong team)
    {
        var tactics = new TeamTacticalUnderstanding(session.Memory, session.Profile, session.ModuleBase);
        if (!tactics.IsSupported) return null;
        IReadOnlyList<PlayerRecord> squad;
        int[]? values;
        int? roles;
        try { squad = session.ReadSquad(team); values = tactics.Read(team, squad); roles = tactics.ReadRoles(squad); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return null; }
        if (values is null) return null;
        var refreshers = new List<Action<int[], int?>>();
        void Refresh()
        {
            if (tactics.Read(team, squad) is { } now)
            {
                int? roleNow = tactics.ReadRoles(squad);
                foreach (var refresh in refreshers) refresh(now, roleNow);
            }
        }
        void Run(string label, Func<int> write)
        {
            try
            {
                int changed = 0;
                backend.Edits.Execute("Club", club.DisplayName, label, () => changed = write());
                status.Text = Loc.F("Tactical understanding: {0} value(s) changed. Continue in FM to see updates.", changed);
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
            Refresh();
        }
        Control Bar(string name, int initial, Func<int, int> write, Func<int[], int?, int?> read)
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5, TickFrequency = 1, IsSnapToTickEnabled = true, Value = initial, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Text = initial.ToString(Inv), Width = 52, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            int applied = initial;
            bool syncing = false;
            void Show(int value)
            {
                syncing = true;
                slider.Value = value;
                box.Text = value.ToString(Inv);
                syncing = false;
            }
            void Apply(int value)
            {
                if (value == applied) { Show(value); return; }
                Run(name, () => write(value));
            }
            refreshers.Add((now, roleNow) => { if (read(now, roleNow) is { } value) { applied = value; Show(value); } });
            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty && !syncing) box.Text = ((int)slider.Value).ToString(Inv);
            };
            slider.AddHandler(Avalonia.Input.InputElement.PointerReleasedEvent, (_, _) => Apply((int)slider.Value), Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
            slider.KeyUp += (_, _) => Apply((int)slider.Value);
            void Typed()
            {
                if (int.TryParse(box.Text, System.Globalization.NumberStyles.Integer, Inv, out int value) && value is >= 0 and <= 100) Apply(value);
                else { status.Text = Loc.S("Value is outside the allowed range"); Show(applied); }
            }
            box.LostFocus += (_, _) => Typed();
            box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Typed(); e.Handled = true; } };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,60"), MinHeight = 32 };
            var label = new TextBlock { Text = Loc.S(name), FontSize = 13, Foreground = UiKit.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(box, 2);
            box.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(label);
            row.Children.Add(slider);
            row.Children.Add(box);
            return row;
        }
        var rows = new StackPanel { Spacing = 2, Margin = new Thickness(12, 8) };
        void Heading(string text) => rows.Children.Add(new TextBlock { Text = Loc.S(text), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = UiKit.TextSecondary, Margin = new Thickness(0, 6, 0, 2) });
        string? group = null;
        foreach (var (barGroup, name, list) in TeamTacticalUnderstanding.Bars)
        {
            if (barGroup != group) Heading(group = barGroup);
            rows.Children.Add(Bar(name, values[list], value => tactics.Set(team, squad, list, value, session.AllocateGameBlock), (now, _) => now[list]));
        }
        if (roles is { } roleValue)
        {
            Heading("Shapes");
            rows.Children.Add(Bar("Position/Role", roleValue, value => tactics.SetRoles(squad, value), (_, roleNow) => roleNow));
        }
        var max = new Button { Content = Loc.S("Max"), Classes = { "accent" }, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        max.Click += (_, _) => Run("Tactical Understanding", () => tactics.Maximise(team, squad, session.AllocateGameBlock) + tactics.SetRoles(squad, 100));
        rows.Children.Add(max);
        return UiKit.Card("Tactical Understanding", rows);
    }

    /// <summary>FMRTE's Team Cohesion card: the squad average, and Max sets every squad player's cohesion to the top.</summary>
    Control BuildTeamCohesionCard(GameSession session, ClubEntry club, TeamEntry? team)
    {
        var average = new TextBlock { FontSize = 13, Foreground = UiKit.TextPrimary, VerticalAlignment = VerticalAlignment.Center };
        var max = new Button { Content = Loc.S("Max"), Classes = { "accent" } };
        IReadOnlyList<PlayerRecord> squad = [];
        if (team is not null)
        {
            try { squad = session.ReadSquad(team.Address); } catch { }
        }
        var writer = new TeamCohesionWriter(session.Memory, session.Profile, session.ModuleBase);
        void Refresh()
        {
            var values = squad.Select(p => writer.Read(p, club.Address)).Where(v => v is not null).Select(v => (int)v!.Value).ToList();
            average.Text = values.Count == 0 ? "" : Math.Round(values.Average()).ToString(Inv);
        }
        Refresh();
        max.IsEnabled = squad.Count > 0;
        max.Click += (_, _) =>
        {
            int done = 0, failed = 0;
            foreach (var p in squad)
            {
                try { backend.Edits.Execute("Club", club.DisplayName, "Team cohesion", () => writer.Set(p, club.Address, TeamCohesionWriter.Improve)); done++; }
                catch { failed++; }
            }
            Refresh();
            status.Text = failed == 0
                ? Loc.F("Team cohesion maxed for {0} player(s). Continue in FM to see updates.", done)
                : Loc.F("Team cohesion maxed for {0} player(s); {1} could not be changed.", done, failed);
        };
        var row = new DockPanel { Margin = new Thickness(12, 8) };
        DockPanel.SetDock(max, Dock.Right);
        row.Children.Add(max);
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { new TextBlock { Text = Loc.S("Squad average"), FontSize = 13, Foreground = UiKit.TextSecondary, VerticalAlignment = VerticalAlignment.Center }, average } });
        return UiKit.Card("Team Cohesion", row);
    }

    void RenderClubFinancesSection(
        GameSession session,
        ClubEntry club,
        IReadOnlyList<TeamEntry> orderedTeams,
        TeamEntry? activeTeam,
        ulong teamAddr,
        Fm27Editor.Core.Offsets.OffsetProfile profile,
        ClubFieldAccess access,
        IReadOnlyDictionary<ClubField, long> values,
        ClubDraft draft)
    {
        var provider = GameSessionClubListProvider.Default;
        bool hasSponsors = provider.HasReader(session, ClubSection.Sponsors);
        bool hasDebts = provider.HasReader(session, ClubSection.Debts);
        bool hasStatements = session.Profile.Club?.FinanceLists?.Statements is not null;

        if ((selectedFinanceSubSection == ClubSection.Sponsors && !hasSponsors)
            || (selectedFinanceSubSection == ClubSection.Debts && !hasDebts)
            || (selectedFinanceSubSection == ClubSection.IncomeStatement && !hasStatements))
        {
            selectedFinanceSubSection = ClubSection.Finances;
        }

        if (hasSponsors || hasDebts || hasStatements)
        {
            var tabStrip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
            };

            void AddFinanceSubTab(ClubSection subSection, string title)
            {
                bool active = selectedFinanceSubSection == subSection;
                var btn = new Button
                {
                    Content = Loc.S(title),
                };
                if (active)
                    btn.Classes.Add("accent");
                btn.Click += (_, _) =>
                {
                    if (selectedFinanceSubSection == subSection) return;
                    selectedFinanceSubSection = subSection;
                    RenderClubEditor();
                    UpdateClubStatusFromDraft();
                };
                tabStrip.Children.Add(btn);
            }

            AddFinanceSubTab(ClubSection.Finances, "Overview");
            if (hasSponsors)
                AddFinanceSubTab(ClubSection.Sponsors, ClubFieldInfo.SectionTitle(ClubSection.Sponsors));
            if (hasDebts)
                AddFinanceSubTab(ClubSection.Debts, ClubFieldInfo.SectionTitle(ClubSection.Debts));
            if (hasStatements)
                AddFinanceSubTab(ClubSection.IncomeStatement, "Income Statement");

            clubEditorPanel.Children.Add(tabStrip);
        }

        if (selectedFinanceSubSection == ClubSection.Finances)
        {
            RenderClubScalarSection(ClubSection.Finances, club, orderedTeams, teamAddr, profile, access, values, draft);
        }
        else if (selectedFinanceSubSection == ClubSection.IncomeStatement)
        {
            RenderClubIncomeStatement(session, club);
        }
        else
        {
            RenderClubListSection(selectedFinanceSubSection, session, club, orderedTeams, activeTeam, teamAddr);
        }
    }

    /// <summary>Finances → Income Statement: the monthly blocks the game keeps (this month, last month, this season).</summary>
    void RenderClubIncomeStatement(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubFinancialPeriod> periods;
        try { periods = session.FinancialStatements.Read(club.Address); }
        catch (Exception ex) { clubEditorPanel.Children.Add(new TextBlock { Text = UserMessage.For(ex) }); return; }
        if (periods.Count == 0) return;

        string[] headers = ["This Month", "Last Month", "This Season"];
        long currentBalance = 0;
        try { currentBalance = session.ClubFields.Read(club.Address, ClubField.Balance) ?? 0; } catch { }

        IReadOnlyList<long> Values(Func<ClubFinancialPeriod, long> pick) =>
            periods.Take(headers.Length).Select(pick).ToArray();

        // 1. Summary Card
        var summaryRows = new (string Label, IReadOnlyList<long> Values, bool Bold)[]
        {
            ("Balance", [currentBalance, 0, 0], false),
            ("Profit", Values(p => p.TotalIncome - p.TotalExpenditure), true),
        };
        clubEditorPanel.Children.Add(UiKit.FinancialStatementCard.Create("Summary", headers, summaryRows));

        // 2. Income Card
        var incomeRows = new (string Label, IReadOnlyList<long> Values, bool Bold)[]
        {
            ("Gate Receipts", Values(p => p.GateReceipts), false),
            ("Other", Values(p => p.OtherIncome), false),
            ("Season Tickets", Values(p => p.SeasonTickets), false),
            ("Interest", Values(p => p.Interest), false),
            ("Investments", Values(p => p.Investments), false),
            ("Merchandising", Values(p => p.Merchandising), false),
            ("Players Sold", Values(p => p.PlayersSold), false),
            ("Sponsorship", Values(p => p.Sponsorship), false),
            ("Match Day Income", Values(p => p.MatchDayIncome), false),
            ("Grants", Values(p => p.Grants), false),
            ("Total Income", Values(p => p.TotalIncome), true),
        };
        clubEditorPanel.Children.Add(UiKit.FinancialStatementCard.Create("Income", headers, incomeRows));

        // 3. Expenditure Card
        var expenditureRows = new (string Label, IReadOnlyList<long> Values, bool Bold)[]
        {
            ("Other", Values(p => p.Other), false),
            ("Loan Repayments and Interest", Values(p => p.LoanRepayment), false),
            ("League Fines", Values(p => p.LeagueFines), false),
            ("Ground Maintenance", Values(p => p.GroundMaintenance), false),
            ("Player Wages", Values(p => p.PlayerWages), false),
            ("Staff Wages", Values(p => p.StaffWages), false),
            ("Bonuses", Values(p => p.Bonuses), false),
            ("Loyalty Bonuses", Values(p => p.LoyaltyBonuses), false),
            ("Transfer Expenditure", Values(p => p.TransferExpenditure), false),
            ("VAT", Values(p => p.Vat), false),
            ("NI Employer Tax", Values(p => p.NiEmployerTax), false),
            ("Total Expenditure", Values(p => p.TotalExpenditure), true),
        };
        clubEditorPanel.Children.Add(UiKit.FinancialStatementCard.Create("Expenditure", headers, expenditureRows));

        // FMRTE only shows these figures; here a single figure can be written for one period, guarded by a snapshot check.
        var periodOptions = periods.Select(p => new StatementPeriodOption(p.Index, headers.Length > p.Index ? Loc.S(headers[p.Index]) : Loc.F("Period {0}", p.Index))).ToArray();
        var periodBox = new ComboBox { ItemsSource = periodOptions, SelectedIndex = 0, Width = 150 };
        var fields = new List<StatementFieldOption>();
        foreach (var key in ClubStatementOffsets.IncomeKeys)
            fields.Add(new StatementFieldOption(true, key, Loc.S(StatementFieldLabel(key, true))));
        foreach (var key in ClubStatementOffsets.ExpenditureKeys)
            fields.Add(new StatementFieldOption(false, key, Loc.S(StatementFieldLabel(key, false))));
        var fieldBox = new ComboBox { ItemsSource = fields, SelectedIndex = 0, Width = 240 };
        var valueBox = new TextBox { Text = "0", Width = 140, HorizontalContentAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = Loc.S("Write value") };
        apply.Click += (_, _) =>
        {
            if (periodBox.SelectedItem is not StatementPeriodOption periodOption
                || fieldBox.SelectedItem is not StatementFieldOption field)
            { status.Text = Loc.S("Choose a period and a field."); return; }
            if (!long.TryParse(valueBox.Text, out long value)) { status.Text = Loc.S("Enter a whole number."); return; }
            var period = periods.FirstOrDefault(p => p.Index == periodOption.Index);
            if (period is null) { status.Text = Loc.S("That period is no longer listed."); return; }
            long expected = field.Income ? IncomeValue(period, field.Key) : ExpenditureValue(period, field.Key);
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Finances", () => session.FinancialStatements.Set(club.Address, period.Index, field.Income, field.Key, expected, value));
                RenderClubEditor();
                status.Text = Loc.S("Saved. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        var editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editor.Children.Add(new TextBlock { Text = Loc.S("Period"), VerticalAlignment = VerticalAlignment.Center });
        editor.Children.Add(periodBox);
        editor.Children.Add(new TextBlock { Text = Loc.S("Field"), VerticalAlignment = VerticalAlignment.Center });
        editor.Children.Add(fieldBox);
        editor.Children.Add(new TextBlock { Text = Loc.S("Value"), VerticalAlignment = VerticalAlignment.Center });
        editor.Children.Add(valueBox);
        editor.Children.Add(apply);
        clubEditorPanel.Children.Add(editor);
    }

    sealed record StatementPeriodOption(int Index, string Label)
    {
        public override string ToString() => Label;
    }

    sealed record StatementFieldOption(bool Income, string Key, string Label)
    {
        public override string ToString() => Label;
    }

    static string StatementFieldLabel(string key, bool income) => key switch
    {
        "gateReceipts" => "Gate Receipts",
        "other" => income ? "Other income" : "Other expenditure",
        "seasonTickets" => "Season Tickets",
        "interest" => "Interest",
        "investments" => "Investments",
        "merchandising" => "Merchandising",
        "playersSold" => "Players Sold",
        "sponsorship" => "Sponsorship",
        "matchDayIncome" => "Match Day Income",
        "grants" => "Grants",
        "loanRepayment" => "Loan Repayments and Interest",
        "leagueFines" => "League Fines",
        "groundMaintenance" => "Ground Maintenance",
        "playerWages" => "Player Wages",
        "staffWages" => "Staff Wages",
        "bonuses" => "Bonuses",
        "loyaltyBonuses" => "Loyalty Bonuses",
        "transferExpenditure" => "Transfer Expenditure",
        "vat" => "VAT",
        "niEmployerTax" => "NI Employer Tax",
        _ => key,
    };

    static long IncomeValue(ClubFinancialPeriod p, string key) => key switch
    {
        "gateReceipts" => p.GateReceipts,
        "other" => p.OtherIncome,
        "seasonTickets" => p.SeasonTickets,
        "interest" => p.Interest,
        "investments" => p.Investments,
        "merchandising" => p.Merchandising,
        "playersSold" => p.PlayersSold,
        "sponsorship" => p.Sponsorship,
        "matchDayIncome" => p.MatchDayIncome,
        "grants" => p.Grants,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    static long ExpenditureValue(ClubFinancialPeriod p, string key) => key switch
    {
        "other" => p.Other,
        "loanRepayment" => p.LoanRepayment,
        "leagueFines" => p.LeagueFines,
        "groundMaintenance" => p.GroundMaintenance,
        "playerWages" => p.PlayerWages,
        "staffWages" => p.StaffWages,
        "bonuses" => p.Bonuses,
        "loyaltyBonuses" => p.LoyaltyBonuses,
        "transferExpenditure" => p.TransferExpenditure,
        "vat" => p.Vat,
        "niEmployerTax" => p.NiEmployerTax,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    Control BuildClubTeamSelector(ClubEntry club, IReadOnlyList<TeamEntry> orderedTeams, ulong teamAddr)
    {
        var teamOptions = orderedTeams.Select((t, i) => new TeamOption(t, i)).ToArray();
        var selectedOpt = teamOptions.FirstOrDefault(o => o.Team.Address == teamAddr) ?? teamOptions[0];
        var teamCombo = new ComboBox
        {
            Width = 240,
            ItemsSource = teamOptions,
            SelectedItem = selectedOpt,
        };
        teamCombo.SelectionChanged += (_, _) =>
        {
            if (teamCombo.SelectedItem is TeamOption chosen && chosen.Team.Address != teamAddr)
            {
                clubSelectedTeam[club.Address] = chosen.Team.Address;
                RenderClubEditor();
                UpdateClubStatusFromDraft();
            }
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.S("Team"),
                    FontSize = 13,
                    Foreground = Brush("#CBD5E1"),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                teamCombo,
            },
        };
    }

    async void RenderClubListSection(
        ClubSection listSection,
        GameSession session,
        ClubEntry club,
        IReadOnlyList<TeamEntry> orderedTeams,
        TeamEntry? activeTeam,
        ulong teamAddr)
    {
        var version = clubRenderGeneration;
        if (listSection == ClubSection.Kits)
        {
            RenderClubKits(session, club);
            return;
        }
        if (listSection == ClubSection.Relationships)
        {
            RenderClubRelationships(session, club);
            return;
        }
        if (listSection == ClubSection.ClubVision && session.Profile.Club?.Lists?.VisionImportanceEditable == true)
        {
            if (teamAddr != 0) RenderClubVision(session, club, orderedTeams, teamAddr);
            return;
        }
        if (listSection == ClubSection.Affiliations && session.Profile.Club?.Lists?.AffiliationAnnualFee is not null)
        {
            RenderClubAffiliations(session, club);
            return;
        }
        if (listSection == ClubSection.Debts && session.Profile.Club?.FinanceLists is { MonthlyRepayment: not null, MonthlyInterestRepayment: not null })
        {
            RenderClubDebts(session, club);
            return;
        }
        if (listSection == ClubSection.RegionalDivisions && session.Profile.Club?.Lists is { RegionalSize: >= 0xA })
        {
            RenderClubRegionalDivisions(session, club);
            return;
        }
        if (listSection == ClubSection.Sponsors && session.Profile.Club?.FinanceLists is { SponsorTotal: >= 0 })
        {
            RenderClubSponsors(session, club);
            return;
        }
        if (listSection == ClubSection.Staff)
        {
            Fm27Editor.Core.Staff.StaffRecord[] staffMembers;
            try
            {
                staffMembers = await Task.Run(() =>
                    session.Staff.LoadAll().Where(s => s.Club?.Address == club.Address).ToArray());
            }
            catch (Exception ex) { if (version == clubRenderGeneration && clubsActive) status.Text = UserMessage.For(ex); return; }
            if (version != clubRenderGeneration || !clubsActive || selectedClub?.Address != club.Address || selectedClubSection != listSection) return;
            if (staffMembers.Length == 0) return;
            // The same table as a nation's Staff: ticks, header sorting, and Tools that act on the club contract only.
            var clubScope = new ClubStaffScope(session);
            var today = session.ReadGameDate();
            var staffLines = staffMembers.Select(s => new StaffListRow(s,
                s.JobCode is { } j ? CareerPlanLabels.Name(CareerPlanLabels.Jobs, j) : "", s.TeamName ?? "", s.Nationality?.DisplayName ?? "",
                ClubStaffLabels.Age(s.DateOfBirth, today), s.CurrentAbility, s.PotentialAbility, clubScope.ExpiryText(s))).ToList();
            clubEditorPanel.Children.Add(new Border
            {
                Margin = new Thickness(12, 0, 0, 0),
                Child = BuildStaffTable(staffLines, clubScope, RenderClubEditor, club.Address),
            });
            return;
        }        var provider = GameSessionClubListProvider.Default;
        var playerRecords = rows.OfType<PlayerRecord>().ToArray();
        ClubListTableModel table;
        IReadOnlyList<ClubTransferOffer>? transferOffers = null;
        if (listSection == ClubSection.TransferCenter)
        {
            try
            {
                transferOffers = await Task.Run(() =>
                {
                    var staff = backend.Data?.Staff ?? session.Staff.LoadAll();
                    return session.Proposals.ReadForClub(club.Address, playerRecords, staff);
                });
                table = ClubListBuilder.BuildTransferCenter(transferOffers.Select(o => new ClubTransferOfferRow(
                    o.Proposal.Id, o.PersonName, o.PersonId, o.Proposal.From, BuyerClub: o.Proposal.To,
                    Fee: o.Proposal.Value, StatusCode: (byte)o.Proposal.Status, TypeName: o.Proposal.Type.ToString())));
            }
            catch (Exception ex) { if (version == clubRenderGeneration && clubsActive) status.Text = UserMessage.For(ex); return; }
            if (version != clubRenderGeneration || !clubsActive || selectedClub?.Address != club.Address || selectedClubSection != listSection) return;
        }
        else
        {
            if (!provider.HasReader(session, listSection)) return;
            try
            {
                table = await Task.Run(() =>
                {
                    if (competitionCache is null && playerRecords.Length > 0 && provider.NeedsCompetitions(session, listSection))
                        competitionCache = session.LoadCompetitions(playerRecords);
                    return provider.ReadSection(listSection, session, club, activeTeam, loadedClubs, competitionCache, playerRecords);
                });
            }
            catch (Exception ex) { if (version == clubRenderGeneration && clubsActive) status.Text = UserMessage.For(ex); return; }
            if (version != clubRenderGeneration || !clubsActive || selectedClub?.Address != club.Address || selectedClubSection != listSection) return;
        }

        if (!table.HasReader)
            return;

        if (table.Rows.Count == 0)
            return;

        var selKey = (club.Address, listSection);
        int selectedIdx = clubListSelectedRow.TryGetValue(selKey, out int savedIdx)
            ? Math.Clamp(savedIdx, 0, table.Rows.Count - 1)
            : 0;
        clubListSelectedRow[selKey] = selectedIdx;

        Control? Detail(int index) => transferOffers is null ? BuildClubListDetailCard(table.Rows[index]) : BuildClubTransferDetail(session, transferOffers[index]);
        var detailHost = new Border
        {
            Child = Detail(selectedIdx),
        };

        AddClubListSection(BuildClubListTable(table, selectedIdx, newIdx =>
        {
            clubListSelectedRow[selKey] = newIdx;
            detailHost.Child = Detail(newIdx);
        }), detailHost);
    }

    /// <summary>
    /// Names for the competition items of Club Vision. The item number is the competition's <c>Index</c>, not its <c>Id</c>:
    /// FMRTE shows index 637 as UEFA Champions League (id 1301394), 51 as LALIGA EA SPORTS (id 67) and 658 as the Spanish cup.
    /// </summary>
    internal static Dictionary<int, string> VisionCompetitionNames(IEnumerable<Fm27Editor.Core.Competitions.CompetitionRecord> competitions) =>
        competitions.Where(c => !string.IsNullOrEmpty(c.DisplayName)).GroupBy(c => c.Index).ToDictionary(g => g.Key, g => g.First().DisplayName);

    async void RenderClubVision(GameSession session, ClubEntry club, IReadOnlyList<TeamEntry> orderedTeams, ulong teamAddr)
    {
        var version = clubRenderGeneration;
        var section = selectedClubSection;
        IReadOnlyList<ClubVisionEntry> entries;
        try
        {
            // The reads run in the background: the name lists are built once per save (8,000+ competitions, 230 nations),
            // and the vision itself is read from the game; the window stays responsive meanwhile.
            var competitions = visionCompetitionNames;
            var nations = visionNationNames;
            var continents = visionContinentNames;
            var clubs = loadedClubs;
            var result = await Task.Run(() =>
            {
                // An item index means a competition's index, a nation's position, a continent's position or a club, by its marker.
                competitions ??= VisionCompetitionNames((backend.Data?.Competitions ?? session.Competitions.LoadAll()));
                if (nations is null || continents is null)
                {
                    var nationList = (backend.Data?.Nations ?? session.Nations.LoadAll());
                    nations ??= new Dictionary<int, string>(); // No verified nation index: preserve raw unknown target.
                    continents ??= new Dictionary<int, string>(); // Sorted display order is not a game index.
                }
                return session.ReadClubVision(teamAddr, clubs, competitions, nations, continents);
            });
            visionCompetitionNames = competitions;
            visionNationNames = nations;
            visionContinentNames = continents;
            entries = result;
        }
        catch (Exception ex) { if (version == clubRenderGeneration) status.Text = UserMessage.For(ex); return; }
        // The user may have moved to another club or section while the vision was being read.
        if (version != clubRenderGeneration || !clubsActive || selectedClub?.Address != club.Address || selectedClubSection != section) return;
        if (entries.Count == 0) return;

        if (!clubVisionDrafts.TryGetValue(teamAddr, out var drafts))
            clubVisionDrafts[teamAddr] = drafts = [];

        var rows = entries.Select(r =>
        {
            var d = drafts.GetValueOrDefault(r.Address);
            byte typeCode = d?.TypeCode ?? r.TypeCode;
            int importance = d?.Importance ?? r.Importance;
            DateOnly? start = d?.Dates is { } dt ? dt.Start : r.StartDate;
            DateOnly? end = d?.Dates is { } dtEnd ? dtEnd.End : r.EndDate;
            int? source = d?.SourceType ?? r.SourceType;
            return new ClubVisionRow(
                typeCode, (byte)importance, start, end, r.ItemName ?? r.ClubItem?.DisplayName, r.ItemIndex,
                TypeName: ClubVisionTypes.All.TryGetValue(typeCode, out var visionText) ? Loc.S(visionText) : Loc.F("Unknown ({0})", typeCode),
                Target: r.Target,
                SourceType: source,
                ForCompetition: r.ItemKind == ClubVisionItemKind.Competition);
        });

        var table = ClubListBuilder.BuildVision(rows, usesTeamRoot: true);
        var key = (club.Address, ClubSection.ClubVision);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, entries.Count - 1) : 0;
        clubListSelectedRow[key] = selected;

        Button? saveBtn = null;
        void UpdateSaveState()
        {
            bool dirty = drafts.Values.Any(d => d.HasChanges);
            if (saveBtn is not null) saveBtn.IsEnabled = dirty && drafts.Values.All(d => d.Invalid.Count == 0);
            UpdateClubStatusFromDraft();
        }

        Control Detail(int index)
        {
            var entry = entries[index];
            var d = drafts.GetValueOrDefault(entry.Address);
            var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8) };
            var targetHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Left };

            int curImportance = d?.Importance ?? entry.Importance;
            var box = new TextBox { Text = d?.Inputs.GetValueOrDefault("Importance", curImportance.ToString(Inv)) ?? curImportance.ToString(Inv), Width = 100, HorizontalContentAlignment = HorizontalAlignment.Right };
            box.TextChanged += (_, _) =>
            {
                if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                rowDraft.Inputs["Importance"] = box.Text ?? "";
                if (int.TryParse(box.Text, out int v) && v is >= 0 and <= 10)
                {
                    rowDraft.Invalid.Remove("Importance");
                    rowDraft.Importance = (v == entry.Importance) ? null : v;
                }
                else rowDraft.Invalid.Add("Importance");
                UpdateSaveState();
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Importance"), Width = 90, VerticalAlignment = VerticalAlignment.Center }, box
            } });

            var types = ClubVisionTypes.All.Where(kv => kv.Key == entry.TypeCode ||
                (kv.Key is not (88 or 89) && ClubVisionTypes.TargetKind(kv.Key) == ClubVisionTypes.TargetKind(entry.TypeCode)))
                .Select(kv => (Code: kv.Key, Name: Loc.S(kv.Value))).ToList();
            byte curTypeCode = d?.TypeCode ?? entry.TypeCode;
            if (!ClubVisionTypes.All.ContainsKey(curTypeCode))
                types.Insert(0, (curTypeCode, Loc.F("Unknown ({0})", curTypeCode)));
            var typeCombo = new ComboBox
            {
                Width = 380,
                ItemsSource = types.Select(t => t.Name).ToArray(),
                SelectedIndex = types.FindIndex(t => t.Code == curTypeCode),
            };
            typeCombo.SelectionChanged += (_, _) =>
            {
                if (typeCombo.SelectedIndex >= 0 && typeCombo.SelectedIndex < types.Count)
                {
                    byte code = types[typeCombo.SelectedIndex].Code;
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.TypeCode = (code == entry.TypeCode) ? null : code;
                    rowDraft.ItemTarget = null;
                    rowDraft.ItemTargetName = null;
                    rowDraft.Target = null;
                    rowDraft.Inputs.Remove("Target"); rowDraft.Invalid.Remove("Target");
                    RefreshTargetRow();
                    UpdateSaveState();
                }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Objective"), Width = 90, VerticalAlignment = VerticalAlignment.Center }, typeCombo
            } });

            var sourceOptions = new (int Code, string Name)[]
            {
                (1, Loc.S("Board")),
                (entry.SourceType is { } st && st != 1 ? st : 0, entry.SourceType is { } unknown && unknown != 1 ? Loc.F("Unknown ({0})", unknown) : Loc.S("Unknown"))
            };
            int curSource = d?.SourceType ?? entry.SourceType ?? 0;
            var sourceCombo = new ComboBox
            {
                Width = 140,
                ItemsSource = sourceOptions.Select(o => o.Name).ToArray(),
                SelectedIndex = curSource == 1 ? 0 : 1,
            };
            sourceCombo.SelectionChanged += (_, _) =>
            {
                if (sourceCombo.SelectedIndex >= 0)
                {
                    int code = sourceOptions[sourceCombo.SelectedIndex].Code;
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.SourceType = (code == (entry.SourceType ?? 0)) ? null : code;
                    UpdateSaveState();
                }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Source"), Width = 90, VerticalAlignment = VerticalAlignment.Center }, sourceCombo
            } });

            panel.Children.Add(targetHost);
            RefreshTargetRow();

            void RefreshTargetRow()
            {
                targetHost.Content = null;
                var rowDraft = drafts.GetValueOrDefault(entry.Address);
                byte code = rowDraft?.TypeCode ?? entry.TypeCode;
                string? rawTargetLabel = ClubVisionTypes.TargetLabel(code);
                if (rawTargetLabel is null) return;
                string targetLabel = Loc.S(rawTargetLabel);
                var targetKind = ClubVisionTypes.TargetItemKind(code);
                bool sameType = code == entry.TypeCode;
                if (targetKind != ClubVisionItemKind.Unknown)
                {
                    ushort? index = rowDraft?.ItemTarget?.Index ?? (sameType ? entry.ItemIndex is { } i ? (ushort)i : null : null);
                    string targetValue = rowDraft?.ItemTargetName ?? (sameType ? entry.ItemName ?? entry.ClubItem?.DisplayName : null)
                        ?? index?.ToString(Inv) ?? "";
                    var valueText = new TextBlock { Text = targetValue, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#CBD5E1") };
                    var search = new Button { Content = Loc.S("Search"), FontSize = 11, Padding = new Thickness(8, 0), MinHeight = 24, IsEnabled = targetKind is ClubVisionItemKind.Competition or ClubVisionItemKind.Club };
                    search.Click += async (_, _) =>
                    {
                        ClubVisionTargetChoice? picked = null;
                        if (targetKind == ClubVisionItemKind.Competition)
                        {
                            var list = (backend.Data?.Competitions ?? session.Competitions.LoadAll());
                            picked = await PickerDialog.Show(this, Loc.S("Choose competition"), q => list
                                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName) && c.Index is >= 0 and <= short.MaxValue)
                                .Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id))
                                .Take(200).Select(c => (c.DisplayName, new ClubVisionTargetChoice(c.DisplayName, (ushort)c.Index))).ToArray(),
                                Loc.S("Competition name"));
                        }
                        else if (targetKind == ClubVisionItemKind.Club)
                        {
                            var list = loadedClubs;
                            picked = await PickerDialog.Show(this, Loc.S("Choose club"), q => list
                                .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                                .Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id))
                                .Take(200).Select(c => (c.DisplayName, new ClubVisionTargetChoice(c.DisplayName, ClubIndex(session, c)))).ToArray(),
                                Loc.S("Club name"));
                        }
                        if (picked is null) return;
                        if (!drafts.TryGetValue(entry.Address, out var draft)) drafts[entry.Address] = draft = new();
                        draft.ItemTarget = (targetKind, picked.Index);
                        draft.ItemTargetName = picked.Name;
                        valueText.Text = picked.Name;
                        UpdateSaveState();
                    };
                    targetHost.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = targetLabel, Width = 90, VerticalAlignment = VerticalAlignment.Center },
                        valueText, search
                    } };
                }
                else
                {
                    int current = rowDraft?.Target ?? (sameType ? entry.Target ?? 0 : 0);
                    var targetBox = new TextBox { Text = rowDraft?.Inputs.GetValueOrDefault("Target", current.ToString(Inv)) ?? current.ToString(Inv), Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right };
                    targetBox.TextChanged += (_, _) =>
                    {
                        if (!drafts.TryGetValue(entry.Address, out var draft)) drafts[entry.Address] = draft = new();
                        draft.Inputs["Target"] = targetBox.Text ?? "";
                        if (int.TryParse(targetBox.Text, out int value) && value >= 0)
                        { draft.Invalid.Remove("Target"); draft.Target = value; }
                        else draft.Invalid.Add("Target");
                        UpdateSaveState();
                    };
                    targetHost.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = targetLabel, Width = 90, VerticalAlignment = VerticalAlignment.Center }, targetBox
                    } };
                }
            }
            DateOnly? curStart = d?.Dates is { } dtStart ? dtStart.Start : entry.StartDate;
            DateOnly? curEnd = d?.Dates is { } dtEnd ? dtEnd.End : entry.EndDate;
            var startBox = new DateBox { Text = d?.Inputs.GetValueOrDefault("Start", DateSetting.Format(curStart)) ?? DateSetting.Format(curStart), Width = 130 };
            var endBox = new DateBox { Text = d?.Inputs.GetValueOrDefault("End", DateSetting.Format(curEnd)) ?? DateSetting.Format(curEnd), Width = 130 };
            void OnDatesChanged()
            {
                if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                rowDraft.Inputs["Start"] = startBox.Text ?? ""; rowDraft.Inputs["End"] = endBox.Text ?? "";
                if (DateSetting.TryParse(startBox.Text, out var s) && DateSetting.TryParse(endBox.Text, out var e)
                    && s.Year is >= 1901 and <= 2300 && e.Year is >= 1901 and <= 2300 && e >= s)
                {
                    rowDraft.Invalid.Remove("Dates");
                    rowDraft.Dates = (s == entry.StartDate && e == entry.EndDate) ? null : (s, e);
                }
                else rowDraft.Invalid.Add("Dates");
                UpdateSaveState();
            }
            startBox.TextChanged += (_, _) => OnDatesChanged();
            endBox.TextChanged += (_, _) => OnDatesChanged();
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Start"), VerticalAlignment = VerticalAlignment.Center }, startBox,
                new TextBlock { Text = Loc.S("End"), VerticalAlignment = VerticalAlignment.Center }, endBox
            } });

            saveBtn = new Button { IsVisible = false, Content = Loc.S("Save changes"), Classes = { "accent" }, IsEnabled = drafts.Values.Any(r => r.HasChanges) && drafts.Values.All(r => r.Invalid.Count == 0) };
            saveBtn.Click += (_, _) =>
            {
                if (drafts.Values.Any(r => r.Invalid.Count != 0)) { status.Text = Loc.S("Value is outside the allowed range"); return; }
                var edits = new List<ClubVisionEdit>();
                foreach (var (addr, rowDraft) in drafts)
                {
                    var ent = entries.FirstOrDefault(e => e.Address == addr);
                    if (ent is null || !rowDraft.HasChanges) continue;
                    edits.Add(new(ent, rowDraft.Importance, rowDraft.TypeCode, rowDraft.SourceType,
                        rowDraft.Dates, rowDraft.ItemTarget, rowDraft.Target));
                }
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Vision", () => session.ClubVisionWriter.ApplyEdits(teamAddr, edits));
                    drafts.Clear();
                    RenderClubEditor();
                    status.Text = Loc.S("Saved club vision. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            var duplicate = new Button { Content = Loc.S("Duplicate row") };
            duplicate.Click += (_, _) =>
            {
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Vision: Duplicate", () => session.ClubVisionWriter.Duplicate(teamAddr, entry, session.AllocateGameBlock));
                    drafts.Clear();
                    RenderClubEditor();
                    status.Text = Loc.S("Row added. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            var remove = new Button { Content = Loc.S("Remove") };
            remove.Click += (_, _) =>
            {
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Vision: Remove", () => session.ClubVisionWriter.Remove(teamAddr, entry));
                    drafts.Clear();
                    RenderClubEditor();
                    status.Text = Loc.S("Removed. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            var addBtn = new Button { Content = Loc.S("Add objective") };
            addBtn.Click += async (_, _) =>
            {
                await ShowAddClubVisionObjectiveDialogAsync(session, teamAddr, entries[0]);
            };

            var buttonsBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0), Children = { saveBtn, duplicate, remove, addBtn } };
            panel.Children.Add(buttonsBar);
            return panel;
        }

        var detail = new Border { Child = Detail(selected) };
        AddClubListSection(BuildClubListTable(table, selected, index => { clubListSelectedRow[key] = index; detail.Child = Detail(index); }), detail);
    }

    async Task ShowAddClubVisionObjectiveDialogAsync(GameSession session, ulong teamAddr, ClubVisionEntry template)
    {
        var types = ClubVisionTypes.All.Where(kv => kv.Key is not (88 or 89) &&
            ClubVisionTypes.TargetItemKind(kv.Key) is not (ClubVisionItemKind.Nation or ClubVisionItemKind.Continent))
            .Select(kv => (Code: kv.Key, Name: Loc.S(kv.Value))).ToList();
        var typeCombo = new ComboBox { ItemsSource = types.Select(t => t.Name).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var importanceBox = new TextBox { Text = "5", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right, HorizontalAlignment = HorizontalAlignment.Left };
        var targetBox = new TextBox { Text = "0", Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right, HorizontalAlignment = HorizontalAlignment.Left };
        var targetNameText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#CBD5E1") };
        var targetButton = new Button { Content = Loc.S("Search"), IsVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var targetContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Children = { targetBox, targetNameText, targetButton } };
        ushort? targetIndex = null;
        string? targetName = null;
        ClubVisionItemKind targetKind = ClubVisionItemKind.Unknown;
        var today = session.ReadGameDate();
        var startBox = new DateBox { Text = today is { } day ? DateSetting.Format(day) : "", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };
        var endBox = new DateBox { Text = today is { } endDay && endDay.Year < 2300 ? DateSetting.Format(endDay.AddYears(1)) : "", Width = 130, HorizontalAlignment = HorizontalAlignment.Left };

        var dialog = new Window
        {
            Title = Loc.S("Add Club Vision Objective"),
            Width = 680,
            Height = 400,
            MinWidth = 520, MinHeight = 320,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush("#0D0F17"),
        };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowSpacing = 10, ColumnSpacing = 12, Margin = new Thickness(16) };
        TextBlock AddRow(string label, Control field, int row)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = Loc.S(label), TextWrapping = TextWrapping.Wrap, Foreground = Brush("#94A3B8"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, row); Grid.SetColumn(l, 0);
            Grid.SetRow(field, row); Grid.SetColumn(field, 1);
            form.Children.Add(l); form.Children.Add(field);
            return l;
        }

        AddRow("Objective", typeCombo, 0);
        AddRow("Importance", importanceBox, 1);
        var targetRowLabel = AddRow("Target", targetContent, 2);
        AddRow("Start Date", startBox, 3);
        AddRow("End Date", endBox, 4);

        void UpdateTarget()
        {
            if (typeCombo.SelectedIndex < 0 || typeCombo.SelectedIndex >= types.Count) return;
            byte code = types[typeCombo.SelectedIndex].Code;
            string? label = ClubVisionTypes.TargetLabel(code);
            var nextKind = ClubVisionTypes.TargetItemKind(code);
            if (nextKind != targetKind) { targetIndex = null; targetName = null; }
            targetKind = nextKind;
            bool hasTarget = label is not null;
            targetRowLabel.IsVisible = targetContent.IsVisible = hasTarget;
            if (!hasTarget)
            {
                targetIndex = null;
                targetName = null;
                targetKind = ClubVisionItemKind.Unknown;
                return;
            }
            targetRowLabel.Text = Loc.S(label!);
            bool entity = targetKind != ClubVisionItemKind.Unknown;
            targetBox.IsVisible = !entity;
            targetNameText.IsVisible = entity;
            targetButton.IsVisible = entity;
            targetButton.IsEnabled = targetKind is ClubVisionItemKind.Competition or ClubVisionItemKind.Club;
            if (entity) targetNameText.Text = targetName ?? "";
        }
        typeCombo.SelectionChanged += (_, _) => UpdateTarget();
        UpdateTarget();

        targetButton.Click += async (_, _) =>
        {
            if (typeCombo.SelectedIndex < 0 || typeCombo.SelectedIndex >= types.Count) return;
            var kind = ClubVisionTypes.TargetItemKind(types[typeCombo.SelectedIndex].Code);
            ClubVisionTargetChoice? picked = null;
            if (kind == ClubVisionItemKind.Competition)
            {
                var list = (backend.Data?.Competitions ?? session.Competitions.LoadAll());
                picked = await PickerDialog.Show(this, Loc.S("Choose competition"), q =>
                    list.Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                        .Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id))
                        .Take(200).Select(c => (c.DisplayName, new ClubVisionTargetChoice(c.DisplayName, (ushort)c.Index))).ToArray(),
                    Loc.S("Competition name"));
            }
            else if (kind == ClubVisionItemKind.Club)
            {
                var list = loadedClubs;
                picked = await PickerDialog.Show(this, Loc.S("Choose club"), q =>
                    list.Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
                        .Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id))
                        .Take(200).Select(c => (c.DisplayName, new ClubVisionTargetChoice(c.DisplayName, ClubIndex(session, c)))).ToArray(),
                    Loc.S("Club name"));
            }
            if (picked is null) return;
            targetIndex = picked.Index;
            targetName = picked.Name;
            targetNameText.Text = picked.Name;
        };

        var createBtn = new Button { Content = Loc.S("Create objective"), Classes = { "accent" } };
        var cancelBtn = new Button { Content = Loc.S("Cancel") };
        cancelBtn.Click += (_, _) => dialog.Close();

        createBtn.Click += (_, _) =>
        {
            if (typeCombo.SelectedIndex < 0 || typeCombo.SelectedIndex >= types.Count) return;
            byte typeCode = types[typeCombo.SelectedIndex].Code;
            if (!byte.TryParse(importanceBox.Text, out byte imp)) { status.Text = Loc.S("Importance must be a whole number."); return; }
            if (imp > 10) { status.Text = Loc.S("Importance must be 0-10"); return; }   // FMRTE docs: importance 0 to 10
            var kind = ClubVisionTypes.TargetItemKind(typeCode);
            int tgt = 0;
            if (kind != ClubVisionItemKind.Unknown)
            {
                if (targetIndex is null) { status.Text = Loc.F("Choose {0}.", Loc.S(ClubVisionTypes.TargetLabel(typeCode)!).ToLowerInvariant()); return; }
            }
            else if (ClubVisionTypes.TargetLabel(typeCode) is not null)
            {
                if (!int.TryParse(targetBox.Text, out tgt)) { status.Text = Loc.S("Target must be a whole number."); return; }
            }
            if (!DateSetting.TryParse(startBox.Text, out var start)
                || !DateSetting.TryParse(endBox.Text, out var end))
            { status.Text = Loc.F("Dates must be in {0} format.", DateSetting.Hint); return; }

            try
            {
                backend.Edits.Execute("Club", (selectedClub?.DisplayName ?? "Club"), "Vision: Add", () =>
                    session.ClubVisionWriter.Create(teamAddr, template, typeCode, imp, tgt, start, end, session.AllocateGameBlock, targetIndex, kind));
                RenderClubEditor();
                status.Text = Loc.S("Objective added. Continue in FM to see updates.");
                dialog.Close();
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { createBtn, cancelBtn } };
        form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(actions, 5); Grid.SetColumnSpan(actions, 2);
        form.Children.Add(actions);

        dialog.Content = new ScrollViewer { Content = form };
        await dialog.ShowDialog(this);
    }

    static ushort ClubIndex(GameSession session, ClubEntry club)
    {
        var bytes = new byte[4];
        if (!session.Memory.TryRead(club.Address + 8, bytes)) throw new InvalidOperationException("Club index is unavailable");
        int index = BitConverter.ToInt32(bytes, 0);
        return index is >= 0 and <= short.MaxValue ? (ushort)index : throw new InvalidOperationException("Club index is unavailable");
    }


    void RenderClubAffiliations(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubAffiliationEntry> entries;
        try { entries = session.ReadAffiliations(club.Address); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
        if (entries.Count == 0) return;

        if (!clubAffiliationDrafts.TryGetValue(club.Address, out var drafts))
            clubAffiliationDrafts[club.Address] = drafts = [];

        var rows = entries.Select(r =>
        {
            var d = drafts.GetValueOrDefault(r.Address);
            DateOnly? start = d?.StartDate ?? r.StartDate;
            DateOnly? end = d?.EndDate ?? r.EndDate;
            int? fee = d?.AnnualFee ?? r.AnnualFee;
            return new ClubAffiliationRow(
                r.ParentClub?.DisplayName, r.FeederClub?.DisplayName, r.TypeCode, start,
                r.ParentClub?.Id, r.FeederClub?.Id, EndDate: end, AnnualFee: fee);
        });
        var table = ClubListBuilder.BuildAffiliations(rows);
        var key = (club.Address, ClubSection.Affiliations);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, entries.Count - 1) : 0;
        clubListSelectedRow[key] = selected;

        Button? saveBtn = null;
        void UpdateSaveState()
        {
            bool dirty = drafts.Values.Any(d => d.HasChanges) || pendingAffiliationRemovals.ContainsKey(club.Address);
            if (saveBtn is not null) saveBtn.IsEnabled = dirty;
            UpdateClubStatusFromDraft();
        }

        Control Detail(int index)
        {
            if (index < 0 || index >= entries.Count) return new Panel();
            var entry = entries[index];
            var d = drafts.GetValueOrDefault(entry.Address);
            var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 10) };

            // 1. Checkboxes
            var checkBoxes = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 8 };
            if (entry.ScoutingShared is not null)
            {
                bool cur = d?.ScoutingShared ?? entry.ScoutingShared.Value;
                var cb = new CheckBox { Content = Loc.S("Scouting Knowledge Shared"), IsChecked = cur, VerticalAlignment = VerticalAlignment.Center };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.ScoutingShared = (cb.IsChecked == entry.ScoutingShared) ? null : cb.IsChecked;
                    UpdateSaveState();
                };
                checkBoxes.Children.Add(cb);
            }
            if (entry.PlayersOnLoan is not null)
            {
                bool cur = d?.PlayersOnLoan ?? entry.PlayersOnLoan.Value;
                var cb = new CheckBox { Content = Loc.S("Players Go On Loan"), IsChecked = cur, VerticalAlignment = VerticalAlignment.Center };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.PlayersOnLoan = (cb.IsChecked == entry.PlayersOnLoan) ? null : cb.IsChecked;
                    UpdateSaveState();
                };
                checkBoxes.Children.Add(cb);
            }
            if (entry.FirstOption is not null)
            {
                bool cur = d?.FirstOption ?? entry.FirstOption.Value;
                var cb = new CheckBox { Content = Loc.S("First Option To Buy"), IsChecked = cur, VerticalAlignment = VerticalAlignment.Center };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.FirstOption = (cb.IsChecked == entry.FirstOption) ? null : cb.IsChecked;
                    UpdateSaveState();
                };
                checkBoxes.Children.Add(cb);
            }
            if (entry.PlayFriendlies is not null)
            {
                bool cur = d?.PlayFriendlies ?? entry.PlayFriendlies.Value;
                var cb = new CheckBox { Content = Loc.S("Play Friendlies"), IsChecked = cur, VerticalAlignment = VerticalAlignment.Center };
                cb.IsCheckedChanged += (_, _) =>
                {
                    if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                    rowDraft.PlayFriendlies = (cb.IsChecked == entry.PlayFriendlies) ? null : cb.IsChecked;
                    UpdateSaveState();
                };
                checkBoxes.Children.Add(cb);
            }
            if (checkBoxes.Children.Count > 0) panel.Children.Add(checkBoxes);

            // 2. Numeric and Date inputs
            var inputRow = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 16, LineSpacing = 8 };
            if (entry.ParentWagePercent is not null)
            {
                int cur = d?.ParentWagePercent ?? entry.ParentWagePercent.Value;
                var box = new TextBox { Text = cur.ToString(Inv), Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right };
                box.TextChanged += (_, _) =>
                {
                    if (int.TryParse(box.Text, out int v))
                    {
                        if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                        rowDraft.ParentWagePercent = (v == entry.ParentWagePercent) ? null : v;
                        UpdateSaveState();
                    }
                };
                inputRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("% Loan Wage Paid By Parent"), VerticalAlignment = VerticalAlignment.Center }, box
                } });
            }
            if (entry.FriendlyChance is not null)
            {
                int cur = d?.FriendlyChance ?? entry.FriendlyChance.Value;
                var box = new TextBox { Text = cur.ToString(Inv), Width = 90, HorizontalContentAlignment = HorizontalAlignment.Right };
                box.TextChanged += (_, _) =>
                {
                    if (int.TryParse(box.Text, out int v))
                    {
                        if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                        rowDraft.FriendlyChance = (v == entry.FriendlyChance) ? null : v;
                        UpdateSaveState();
                    }
                };
                inputRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("% Chance Of Friendly"), VerticalAlignment = VerticalAlignment.Center }, box
                } });
            }
            if (session.Profile.Club?.Lists?.AffiliationStartEditable == true && entry.StartDate is not null)
            {
                DateOnly cur = d?.StartDate ?? entry.StartDate.Value;
                var box = new DateBox { Text = DateSetting.Format(cur), Width = 130 };
                box.TextChanged += (_, _) =>
                {
                    if (DateSetting.TryParse(box.Text, out var v))
                    {
                        if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                        rowDraft.StartDate = (v == entry.StartDate) ? null : v;
                        UpdateSaveState();
                    }
                };
                inputRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("Start Date"), VerticalAlignment = VerticalAlignment.Center }, box
                } });
            }
            if (entry.EndDate is not null)
            {
                DateOnly cur = d?.EndDate ?? entry.EndDate.Value;
                var box = new DateBox { Text = DateSetting.Format(cur), Width = 130 };
                box.TextChanged += (_, _) =>
                {
                    if (DateSetting.TryParse(box.Text, out var v))
                    {
                        if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                        rowDraft.EndDate = (v == entry.EndDate) ? null : v;
                        UpdateSaveState();
                    }
                };
                inputRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("End Date"), VerticalAlignment = VerticalAlignment.Center }, box
                } });
            }
            if (entry.AnnualFee is not null)
            {
                int cur = d?.AnnualFee ?? entry.AnnualFee.Value;
                var box = new TextBox { Text = cur.ToString(Inv), Width = 110, HorizontalContentAlignment = HorizontalAlignment.Right };
                box.TextChanged += (_, _) =>
                {
                    if (int.TryParse(box.Text, out int v))
                    {
                        if (!drafts.TryGetValue(entry.Address, out var rowDraft)) drafts[entry.Address] = rowDraft = new();
                        rowDraft.AnnualFee = (v == entry.AnnualFee) ? null : v;
                        UpdateSaveState();
                    }
                };
                inputRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("Annual Fee"), VerticalAlignment = VerticalAlignment.Center }, box
                } });
            }
            if (inputRow.Children.Count > 0) panel.Children.Add(inputRow);

            // 3. Actions: Save changes, Remove
            bool isDirty = drafts.Values.Any(r => r.HasChanges) || pendingAffiliationRemovals.ContainsKey(club.Address);
            saveBtn = new Button { IsVisible = false, Content = Loc.S("Save changes"), Classes = { "accent" }, IsEnabled = isDirty };

            bool removing = pendingAffiliationRemovals.TryGetValue(club.Address, out var removal) && removal.Address == entry.Address;
            var removeBtn = new Button { Content = Loc.S(removing ? "Undo Remove" : "Remove"), IsEnabled = removing || !pendingAffiliationRemovals.ContainsKey(club.Address) };

            saveBtn.Click += (_, _) =>
            {
                var actions = new List<Action>();
                foreach (var (addr, rowDraft) in drafts)
                {
                    var ent = entries.FirstOrDefault(e => e.Address == addr);
                    if (ent is null || !rowDraft.HasChanges) continue;
                    if (rowDraft.ScoutingShared is { } sc) actions.Add(() => session.SetAffiliationScoutingShared(club.Address, ent, sc));
                    if (rowDraft.PlayersOnLoan is { } ln) actions.Add(() => session.SetAffiliationPlayersOnLoan(club.Address, ent, ln));
                    if (rowDraft.FirstOption is { } fo) actions.Add(() => session.SetAffiliationFirstOption(club.Address, ent, fo));
                    if (rowDraft.PlayFriendlies is { } pf) actions.Add(() => session.SetAffiliationPlayFriendlies(club.Address, ent, pf));
                    if (rowDraft.ParentWagePercent is { } wp) actions.Add(() => session.SetAffiliationParentWagePercent(club.Address, ent, wp));
                    if (rowDraft.FriendlyChance is { } fc) actions.Add(() => session.SetAffiliationFriendlyChance(club.Address, ent, fc));
                    if (rowDraft.StartDate is { } sd) actions.Add(() => session.SetAffiliationStartDate(club.Address, ent, sd));
                    if (rowDraft.EndDate is { } ed) actions.Add(() => session.SetAffiliationEndDate(club.Address, ent, ed));
                    if (rowDraft.AnnualFee is { } af) actions.Add(() => session.SetAffiliationAnnualFee(club.Address, ent, af));
                }
                if (pendingAffiliationRemovals.TryGetValue(club.Address, out var affRemoval))
                {
                    actions.Add(() => new ClubAffiliationWriter(session.Memory, session.Profile, session.Game.ModuleBase).Remove(club.Address, affRemoval));
                }
                if (actions.Count > 0)
                {
                    try
                    {
                        backend.Edits.Execute("Club", club.DisplayName, "Affiliations", () => { foreach (var act in actions) act(); });
                        drafts.Clear();
                        pendingAffiliationRemovals.Remove(club.Address);
                        RenderClubEditor();
                        status.Text = Loc.S("Saved affiliation changes. Continue in FM to see updates.");
                    }
                    catch (Exception ex) { status.Text = UserMessage.For(ex); }
                }
            };

            removeBtn.Click += (_, _) =>
            {
                if (removing) pendingAffiliationRemovals.Remove(club.Address);
                else pendingAffiliationRemovals[club.Address] = entry;
                UpdateSaveState();
                removeBtn.Content = Loc.S(pendingAffiliationRemovals.ContainsKey(club.Address) ? "Undo Remove" : "Remove");
            };

            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Margin = new Thickness(0, 4, 0, 0),
                Children = { saveBtn, removeBtn }
            });

            return panel;
        }
        var detail = new Border { Child = Detail(selected) };
        clubEditorPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            Children =
            {
                BuildClubListTable(table, selected, index =>
                {
                    clubListSelectedRow[key] = index;
                    detail.Child = Detail(index);
                }),
                detail,
            },
        });
    }

    sealed record SponsorIncomeOption(byte Code, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Competition id -> name for the regional division rows, read once per save in the background.</summary>
    IReadOnlyDictionary<int, string>? regionalCompetitionNames;
    bool regionalCompetitionsLoading;

    sealed record RegionalCompetitionOption(int Id, string Name)
    {
        public override string ToString() => Name;
    }

    /// <summary>Regional Divisions as FMRTE shows them: Name, Level, Year; the selected row's competition, level and year are editable.</summary>
    void RenderClubRegionalDivisions(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubRegionalDivisionEntry> rows;
        try { rows = session.ReadRegionalDivisions(club.Address); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
        if (rows.Count == 0) return;
        if (regionalCompetitionNames is null)
        {
            clubEditorPanel.Children.Add(new TextBlock { Text = Loc.S("Reading competitions…"), Margin = new Thickness(6) });
            if (!regionalCompetitionsLoading)
            {
                regionalCompetitionsLoading = true;
                _ = Task.Run(() => (backend.Data?.Competitions ?? session.Competitions.LoadAll())
                        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                        .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name!))
                    .ContinueWith(t => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        regionalCompetitionsLoading = false;
                        if (t.IsCompletedSuccessfully) { regionalCompetitionNames = t.Result; RenderClubEditor(); }
                        else status.Text = UserMessage.For(t.Exception?.InnerException ?? new InvalidOperationException(Loc.S("Competitions unreadable")));
                    }));
            }
            return;
        }
        var names = regionalCompetitionNames;
        string NameOf(int? id) => id is { } i && names.TryGetValue(i, out var n) ? n : id is { } j ? Loc.F("Competition {0}", j) : "";
        var table = ClubListBuilder.BuildRegionalDivisions(rows.Select(r => new RegionalDivisionRow(r.Level, NameOf(r.CompetitionId), null, r.Year)));
        var key = (club.Address, ClubSection.RegionalDivisions);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, rows.Count - 1) : 0;
        clubListSelectedRow[key] = selected;
        var options = names.Select(p => new RegionalCompetitionOption(p.Key, p.Value)).OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        Control Detail(int index)
        {
            var row = rows[index];
            var competition = new AutoCompleteBox
            {
                Width = 320, FilterMode = AutoCompleteFilterMode.ContainsOrdinal, MinimumPrefixLength = 1,
                ItemsSource = options.Select(o => o.Name).ToList(), Text = NameOf(row.CompetitionId),
            };
            var level = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Math.Clamp((int)row.Level, 0, 100), Width = 110, FormatString = "0" };   // in-game editor: 0-100
            var year = new NumericUpDown { Minimum = 0, Maximum = 100, Value = Math.Clamp((int)(row.Year ?? 0), 0, 100), Width = 110, FormatString = "0" };   // in-game editor: 0-100
            var apply = new Button { Content = Loc.S("Apply") };
            apply.Click += (_, _) =>
            {
                var chosen = options.FirstOrDefault(o => string.Equals(o.Name, competition.Text?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (chosen is null) { status.Text = Loc.S("Choose a competition from the list."); return; }
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Regional division", () =>
                        session.RegionalDivisionWriter.Set(row, chosen.Id, (byte)(level.Value ?? row.Level), (short)(year.Value ?? 0)));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            Control Line(string label, Control input) => new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children = { new TextBlock { Text = Loc.S(label), Width = 90, VerticalAlignment = VerticalAlignment.Center }, input },
            };
            return new StackPanel { Spacing = 8, Children = { Line("Competition", competition), Line("Level", level), Line("Year", year), apply } };
        }
        var detail = new Border { Child = Detail(selected), Margin = new Thickness(0, 8, 0, 0) };
        AddClubListSection(BuildClubListTable(table, selected, i => { clubListSelectedRow[key] = i; detail.Child = Detail(i); }), detail);
    }

    /// <summary>Sponsors and Other: existing records are editable, and records can be added or removed.</summary>
    void RenderClubSponsors(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubSponsorEntry> sponsors;
        try { sponsors = session.ReadSponsors(club.Address); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
        if (sponsors.Count == 0) return;

        var addBar = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 6 };
        var newTotal = new TextBox { Text = "1000000", Width = 112, HorizontalContentAlignment = HorizontalAlignment.Right };
        var newStart = new DateBox { Text = DateSetting.Format(new DateOnly(2026, 7, 1)), Width = 130 };
        var newEnd = new DateBox { Text = DateSetting.Format(new DateOnly(2027, 6, 30)), Width = 130 };
        var newType = new ComboBox
        {
            Width = 196,
            ItemsSource = ClubListLabels.SponsorIncomeTypes
                .Where(pair => ClubSponsorWriter.SupportedIncomeType(pair.Key))
                .Select(pair => new SponsorIncomeOption(pair.Key, Loc.S(pair.Value)))
                .OrderBy(opt => opt.Label, StringComparer.Ordinal).ToArray(),
        };
        newType.SelectedIndex = 0;
        var add = new Button { Content = Loc.S("Add sponsor") };
        add.Click += (_, _) =>
        {
            if (newType.SelectedItem is not SponsorIncomeOption chosen) { status.Text = Loc.S("Choose an income type."); return; }
            if (!int.TryParse(newTotal.Text, out int value)) { status.Text = Loc.S("Enter a whole number for the sponsor value."); return; }
            if (!DateSetting.TryParse(newStart.Text, out var start)
                || !DateSetting.TryParse(newEnd.Text, out var end))
            { status.Text = Loc.F("Enter dates as {0}.", DateSetting.Hint); return; }
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Add", () => session.SponsorWriter.Add(club.Address, chosen.Code, value, start, end));
                RenderClubEditor();
                status.Text = Loc.S("Sponsor added. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        var remove = new Button { Content = Loc.S("Remove selected") };
        remove.Click += (_, _) =>
        {
            int index = clubListSelectedRow.TryGetValue((club.Address, ClubSection.Sponsors), out int saved) ? saved : 0;
            if (index < 0 || index >= sponsors.Count) return;
            try
            {
                var entry = sponsors[index];
                backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Remove", () => session.SponsorWriter.Remove(club.Address, entry));
                RenderClubEditor();
                status.Text = Loc.S("Sponsor removed. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        addBar.Children.Add(new TextBlock { Text = Loc.S("Income Type"), VerticalAlignment = VerticalAlignment.Center });
        addBar.Children.Add(newType);
        addBar.Children.Add(new TextBlock { Text = Loc.S("Value"), VerticalAlignment = VerticalAlignment.Center });
        addBar.Children.Add(newTotal);
        addBar.Children.Add(new TextBlock { Text = Loc.S("Start"), VerticalAlignment = VerticalAlignment.Center });
        addBar.Children.Add(newStart);
        addBar.Children.Add(new TextBlock { Text = Loc.S("End"), VerticalAlignment = VerticalAlignment.Center });
        addBar.Children.Add(newEnd);
        addBar.Children.Add(add);
        addBar.Children.Add(remove);
        clubEditorPanel.Children.Add(addBar);
        var table = ClubListBuilder.BuildSponsors(sponsors.Select(s => new ClubSponsorRow(s.IncomeTypeCode, s.TotalValue, s.StartDate, s.EndDate)));
        var key = (club.Address, ClubSection.Sponsors);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, sponsors.Count - 1) : 0;
        clubListSelectedRow[key] = selected;
        Control Detail(int index)
        {
            var sponsor = sponsors[index];
            var panel = new StackPanel { Spacing = 8 };
            if (ClubSponsorWriter.SupportedIncomeType(sponsor.IncomeTypeCode))
            {
                var options = ClubListLabels.SponsorIncomeTypes
                    .Where(pair => ClubSponsorWriter.SupportedIncomeType(pair.Key))
                    .Select(pair => new SponsorIncomeOption(pair.Key, Loc.S(pair.Value)))
                    .OrderBy(opt => opt.Label, StringComparer.Ordinal).ToArray();
                var income = new ComboBox { ItemsSource = options, SelectedItem = options.First(o => o.Code == sponsor.IncomeTypeCode), Width = 230 };
                var applyIncome = new Button { Content = Loc.S("Apply") };
                applyIncome.Click += (_, _) =>
                {
                    if (income.SelectedItem is not SponsorIncomeOption chosen) return;
                    try
                    {
                        backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Income Type", () => session.SetSponsorIncomeType(club.Address, sponsor, chosen.Code));
                        RenderClubEditor();
                        status.Text = Loc.S("Saved. Continue in FM to see updates.");
                    }
                    catch (Exception ex) { status.Text = UserMessage.For(ex); }
                };
                panel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = Loc.S("Income Type"), VerticalAlignment = VerticalAlignment.Center }, income, applyIncome
                    }
                });
            }
            var totalBox = new TextBox { Text = sponsor.TotalValue.ToString(Inv), Width = 130, HorizontalContentAlignment = HorizontalAlignment.Right };
            var applyTotal = new Button { Content = Loc.S("Apply") };
            applyTotal.Click += (_, _) =>
            {
                if (!int.TryParse(totalBox.Text, out int value)) { status.Text = Loc.S("Enter a whole number for the sponsor value."); return; }
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Total", () => session.SetSponsorTotal(club.Address, sponsor, value));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            panel.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8, Children =
                {
                    new TextBlock { Text = Loc.S("Total Value"), VerticalAlignment = VerticalAlignment.Center }, totalBox, applyTotal
                }
            });
            if (sponsor.StartDate is { } started && sponsor.EndDate is { } ended)
            {
                var startBox = new DateBox { Text = DateSetting.Format(started), Width = 140 };
                var endBox = new DateBox { Text = DateSetting.Format(ended), Width = 140 };
                var applyDates = new Button { Content = Loc.S("Apply") };
                applyDates.Click += (_, _) =>
                {
                    if (!DateSetting.TryParse(startBox.Text, out var start)
                        || !DateSetting.TryParse(endBox.Text, out var end))
                    { status.Text = Loc.F("Enter dates as {0}.", DateSetting.Hint); return; }
                    try
                    {
                        backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Dates", () => session.SetSponsorDates(club.Address, sponsor, start, end));
                        RenderClubEditor();
                        status.Text = Loc.S("Saved. Continue in FM to see updates.");
                    }
                    catch (Exception ex) { status.Text = UserMessage.For(ex); }
                };
                panel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new TextBlock { Text = Loc.S("Start"), VerticalAlignment = VerticalAlignment.Center }, startBox,
                        new TextBlock { Text = Loc.S("End"), VerticalAlignment = VerticalAlignment.Center }, endBox, applyDates
                    }
                });
            }
            if (sponsor.RenewIncome is { } renewIncome && sponsor.FixedValue is { } fixedValue)
            {
                var renewBox = new CheckBox { Content = Loc.S("Renew Income"), IsChecked = renewIncome, VerticalAlignment = VerticalAlignment.Center };
                var fixedBox = new CheckBox { Content = Loc.S("Fixed Value"), IsChecked = fixedValue, VerticalAlignment = VerticalAlignment.Center };
                var applyFlags = new Button { Content = Loc.S("Apply") };
                applyFlags.Click += (_, _) =>
                {
                    try
                    {
                        backend.Edits.Execute("Club", club.DisplayName, "Sponsor: Renew / Fixed", () => session.SetSponsorFlags(club.Address, sponsor, renewBox.IsChecked == true, fixedBox.IsChecked == true));
                        RenderClubEditor();
                        status.Text = Loc.S("Saved. Continue in FM to see updates.");
                    }
                    catch (Exception ex) { status.Text = UserMessage.For(ex); }
                };
                panel.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, Children = { renewBox, fixedBox, applyFlags }
                });
            }
            return panel;
        }
        var detail = new Border { Child = Detail(selected) };
        clubEditorPanel.Children.Add(new StackPanel
        {
            Margin = new Thickness(12, 0, 0, 0),
            Children =
            {
                BuildClubListTable(table, selected, index =>
                {
                    clubListSelectedRow[key] = index;
                    detail.Child = Detail(index);
                }),
                detail,
            },
        });
    }

    void RenderClubDebts(GameSession session, ClubEntry club)    {
        IReadOnlyList<ClubDebtEntry> debts;
        try { debts = session.ReadDebts(club.Address); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
        if (debts.Count == 0) return;
        var table = ClubListBuilder.BuildDebts(debts.Select(d => new ClubDebtRow(d.SourceCode, d.OriginalDebt, d.StartDate, d.EndDate)));
        var key = (club.Address, ClubSection.Debts);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, debts.Count - 1) : 0;
        clubListSelectedRow[key] = selected;
        var detail = new Border { Child = BuildClubDebtDetail(session, club, debts[selected]) };
        AddClubListSection(BuildClubListTable(table, selected, index =>
        {
            clubListSelectedRow[key] = index;
            detail.Child = BuildClubDebtDetail(session, club, debts[index]);
        }), detail);

        var clearAll = new Button { Content = Loc.S("Clear all debts") };
        clearAll.Click += (_, _) =>
        {
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Debts: Remove All", () => session.RemoveAllDebts(club.Address));
                RenderClubEditor();
                status.Text = Loc.S("All debts cleared. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        clubEditorPanel.Children.Add(clearAll);
    }

    sealed record DebtSourceOption(byte Code, string Label)
    {
        public override string ToString() => Loc.S(Label);
    }

    Control BuildClubDebtDetail(GameSession session, ClubEntry club, ClubDebtEntry debt)
    {
        if (debt.MonthlyRepayment is not { } amount || debt.MonthlyInterestRepayment is not { } interest) return new Border();
        var repaymentBox = new TextBox { Text = amount.ToString(Inv), Width = 130, HorizontalContentAlignment = HorizontalAlignment.Right };
        var interestBox = new TextBox { Text = interest.ToString(Inv), Width = 130, HorizontalContentAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = Loc.S("Apply") };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(repaymentBox.Text, out int repayment) || !int.TryParse(interestBox.Text, out int interestValue))
            { status.Text = Loc.S("Enter whole numbers for debt payments."); return; }
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Debt: Repayment / Interest", () => session.SetDebtPayments(club.Address, debt, repayment, interestValue));
                RenderClubEditor();
                status.Text = Loc.S("Saved. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        var panel = new StackPanel { Spacing = 8 };
        if (session.Profile.Club?.FinanceLists?.DebtOriginalEditable == true)
        {
            var originalBox = new TextBox { Text = debt.OriginalDebt.ToString(Inv), Width = 130, HorizontalContentAlignment = HorizontalAlignment.Right };
            var applyOriginal = new Button { Content = Loc.S("Apply") };
            applyOriginal.Click += (_, _) =>
            {
                if (!int.TryParse(originalBox.Text, out int value)) { status.Text = Loc.S("Enter a whole number for original debt."); return; }
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Debt: Original Amount", () => session.SetOriginalDebt(club.Address, debt, value));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Original Debt"), VerticalAlignment = VerticalAlignment.Center }, originalBox, applyOriginal
            } });
        }
        if (session.Profile.Club?.FinanceLists?.DebtDatesEditable == true && debt.StartDate is { } started && debt.EndDate is { } ended)
        {
            var startBox = new DateBox { Text = DateSetting.Format(started), Width = 140 };
            var endBox = new DateBox { Text = DateSetting.Format(ended), Width = 140 };
            var applyDates = new Button { Content = Loc.S("Apply") };
            applyDates.Click += (_, _) =>
            {
                if (!DateSetting.TryParse(startBox.Text, out var start)
                    || !DateSetting.TryParse(endBox.Text, out var end))
                { status.Text = Loc.F("Enter dates as {0}.", DateSetting.Hint); return; }
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Debt: Dates", () => session.SetDebtDates(club.Address, debt, start, end));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Start"), VerticalAlignment = VerticalAlignment.Center }, startBox,
                new TextBlock { Text = Loc.S("End"), VerticalAlignment = VerticalAlignment.Center }, endBox, applyDates
            } });
        }
        if (session.Profile.Club?.FinanceLists?.DebtSourceEditable == true && ClubDebtWriter.SupportedSource(debt.SourceCode))
        {
            var options = ClubListLabels.DebtSources.Select(pair => new DebtSourceOption(pair.Key, pair.Value)).ToArray();
            var source = new ComboBox { ItemsSource = options, SelectedItem = options.First(o => o.Code == debt.SourceCode), Width = 300 };
            var applySource = new Button { Content = Loc.S("Apply") };
            applySource.Click += (_, _) =>
            {
                if (source.SelectedItem is not DebtSourceOption chosen) return;
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Debt: Source", () => session.SetDebtSource(club.Address, debt, chosen.Code));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                new TextBlock { Text = Loc.S("Source"), VerticalAlignment = VerticalAlignment.Center }, source, applySource
            } });
        }
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = Loc.S("Monthly Repayment"), VerticalAlignment = VerticalAlignment.Center }, repaymentBox,
            new TextBlock { Text = Loc.S("Monthly Interest Repayment"), VerticalAlignment = VerticalAlignment.Center }, interestBox, save
        } });
        if (debt.InterestOnly is { } interestOnly)
        {
            var check = new CheckBox { Content = Loc.S("Interest-only payment"), IsChecked = interestOnly, VerticalAlignment = VerticalAlignment.Center };
            var apply = new Button { Content = Loc.S("Apply") };
            apply.Click += (_, _) =>
            {
                try
                {
                    backend.Edits.Execute("Club", club.DisplayName, "Debt: Interest Only", () => session.SetDebtInterestOnly(club.Address, debt, check.IsChecked == true));
                    RenderClubEditor();
                    status.Text = Loc.S("Saved. Continue in FM to see updates.");
                }
                catch (Exception ex) { status.Text = UserMessage.For(ex); }
            };
            panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { check, apply } });
        }
        return panel;
    }

    void RenderClubRelationships(GameSession session, ClubEntry club)
    {
        IReadOnlyList<ClubRelationship> ordered;
        try { ordered = ClubRelationshipLabels.Ordered(session.ReadClubRelationships(club.Address)); }
        catch (Exception ex) { status.Text = UserMessage.For(ex); return; }
        if (ordered.Count == 0) return;
        var key = (club.Address, ClubSection.Relationships);
        int selected = clubListSelectedRow.TryGetValue(key, out int saved) ? Math.Clamp(saved, 0, ordered.Count - 1) : 0;
        clubListSelectedRow[key] = selected;
        var detailHost = new Border { Child = BuildClubRelationshipDetail(session, club, ordered[selected]) };
        AddClubListSection(BuildClubListTable(ClubRelationshipLabels.BuildTable(ordered), selected, index =>
        {
            clubListSelectedRow[key] = index;
            detailHost.Child = BuildClubRelationshipDetail(session, club, ordered[index]);
        }), detailHost);
        clubEditorPanel.Children.Add(BuildClubRelationshipActions(session, club));
    }


    /// <summary>Add a rival club; the guarded writer appends into spare capacity or a new buffer when the vector is full.</summary>
    Control BuildClubRelationshipActions(GameSession session, ClubEntry club)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        IReadOnlyList<ClubEntry> clubs;
        try { clubs = clubCache ?? session.LoadClubs([]); } catch { clubs = []; }
        ClubEntry? chosenRival = null;
        var pick = new Button { Width = 300, HorizontalContentAlignment = HorizontalAlignment.Left, Content = Loc.S("Choose club…") };
        pick.Click += async (_, _) =>
        {
            var choices = clubs.Where(c => c.Address != club.Address && c.Gender != Fm27Editor.Core.Offsets.Gender.Women)
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var chosen = await PickerDialog.Show<ClubEntry>(this, Loc.S("Choose rival club"), q => choices.Where(c => PickerSearch.Hit(q, c.DisplayName, c.ShortName, c.Id)).Select(c => ($"{c.Name} ({c.Id})", c)).ToList());
            if (chosen is null) return;
            chosenRival = chosen;
            pick.Content = $"{chosen.Name} ({chosen.Id})";
        };
        var add = new Button { Content = Loc.S("Add rival") };
        add.Click += (_, _) =>
        {
            if (chosenRival is not { } rival || rival.Address == club.Address) { status.Text = Loc.S("Pick another club"); return; }
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Relationships: Add Rival", () => new Fm27Editor.Core.Clubs.ClubRelationshipWriter(session.Memory, session.Profile, session.Game.ModuleBase).AddRival(club.Address, rival.Address, 50, 2, null));
                RenderClubEditor();
                status.Text = Loc.S("Added. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        row.Children.Add(new TextBlock { Text = Loc.S("Rival"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(pick);
        row.Children.Add(add);
        return row;
    }

    Control BuildClubRelationshipDetail(GameSession session, ClubEntry club, ClubRelationship row)
    {
        if (row.Kind == ClubRelationshipKind.Other) return new Border();
        var value = new TextBox { Text = row.Value.ToString(Inv), Width = 70, HorizontalContentAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = row.Name ?? Loc.S("Unknown"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 });
        panel.Children.Add(new TextBlock { Text = Loc.S("Value"), VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(value);
        ComboBox? reason = null;
        if (row.Kind != ClubRelationshipKind.DislikesPerson)
        {
            reason = new ComboBox { ItemsSource = ClubRelationshipLabels.Reasons(row.Kind), SelectedIndex = Math.Min(row.Reason, ClubRelationshipLabels.Reasons(row.Kind).Count - 1), Width = 170 };
            panel.Children.Add(new TextBlock { Text = Loc.S("Reason"), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(reason);
        }
        CheckBox? permanent = null;
        if (row.Kind != ClubRelationshipKind.Rival)
        {
            permanent = new CheckBox { Content = Loc.S("Permanent"), IsChecked = row.Permanent, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(permanent);
        }
        CheckBox? nameStadium = null;
        if (row.CanNameStadium is { } named)
        {
            nameStadium = new CheckBox { Content = Loc.S("Can Name Stadium After Person"), IsChecked = named, VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(nameStadium);
        }
        var save = new Button { IsVisible = false, Content = Loc.S("Save changes") };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(value.Text, out int v) || v is < 0 or > 100) { status.Text = Loc.S("Value must be 0 to 100"); return; }
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Relationships: Edit", () => session.SetClubRelationship(club.Address, row,
                    reason is { SelectedIndex: >= 0 } r ? r.SelectedIndex : null, permanent?.IsChecked, v, nameStadium?.IsChecked));
                RenderClubEditor();
                status.Text = Loc.S("Saved. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        panel.Children.Add(save);
        var remove = new Button { Content = Loc.S("Remove") };
        remove.Click += (_, _) =>
        {
            try
            {
                backend.Edits.Execute("Club", club.DisplayName, "Relationships: Remove", () => new Fm27Editor.Core.Clubs.ClubRelationshipWriter(session.Memory, session.Profile, session.Game.ModuleBase).Remove(club.Address, row));
                RenderClubEditor();
                status.Text = Loc.S("Removed. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        panel.Children.Add(remove);
        return panel;
    }

    Control BuildClubTransferDetail(GameSession session, ClubTransferOffer offer)
    {
        var value = new TextBox { Text = offer.Proposal.Value.ToString(Inv), Width = 150, HorizontalContentAlignment = HorizontalAlignment.Right };
        var save = new Button { IsVisible = false, Content = Loc.S("Save changes") };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(value.Text, out int fee) || fee is < 0 or > 1_000_000_000) { status.Text = Loc.S("Enter a transfer fee between 0 and 1,000,000,000"); return; }
            try
            {
                backend.Edits.Execute("Club", (selectedClub?.DisplayName ?? "Club"), "Transfer Offer: Fee", () => session.ProposalWriter.SetValue(offer.Proposal, fee));
                RenderClubEditor();
                status.Text = Loc.S("Saved. Continue in FM to see updates.");
            }
            catch (Exception ex) { status.Text = UserMessage.For(ex); }
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            new TextBlock { Text = Loc.S("Value"), VerticalAlignment = VerticalAlignment.Center }, value, save
        } };
    }

    Control BuildClubListTable(ClubListTableModel table, int selectedIdx, Action<int> onRowSelected, Action<int>? onRowDoubleClicked = null)
    {
        var headerGrid = new Grid
        {
            ColumnDefinitions = CreateClubListColumns(table.Columns),
        };
        // Clicking a header sorts the rows; `order[shown] = index in table.Rows`, so selection and double click still address the real row.
        int[] order = Enumerable.Range(0, table.Rows.Count).ToArray();
        int sortColumn = -1;
        bool sortAscending = true;
        var headerLabels = new List<TextBlock>();
        ListBox? sortedList = null;
        void SortBy(int column)
        {
            sortAscending = sortColumn != column || !sortAscending;
            sortColumn = column;
            string Text(int row) => column < table.Rows[row].Cells.Count ? table.Rows[row].Cells[column].Text : "";
            bool numeric = table.Rows.Count > 0 && Enumerable.Range(0, table.Rows.Count).Where(r => Text(r).Length > 0)
                .All(r => double.TryParse(Text(r).Replace(",", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _));
            var sorted = numeric
                ? Enumerable.Range(0, table.Rows.Count).OrderBy(r => double.TryParse(Text(r).Replace(",", ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : double.NegativeInfinity)
                : Enumerable.Range(0, table.Rows.Count).OrderBy(r => Text(r), StringComparer.CurrentCultureIgnoreCase);
            order = (sortAscending ? sorted : sorted.Reverse()).ToArray();
            for (int c = 0; c < headerLabels.Count; c++)
                headerLabels[c].Text = Loc.S(table.Columns[c].Header) + (c == column ? (sortAscending ? " ▲" : " ▼") : "");
            if (sortedList is not null) sortedList.ItemsSource = order.Select(r => table.Rows[r]).ToList();
        }
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            int columnIndex = i;
            var colHeader = new TextBlock
            {
                Text = Loc.S(col.Header),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#CBD5E1"),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = col.RightAlign ? TextAlignment.Right : TextAlignment.Left,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(i == 0 ? 0 : 16, 0, 0, 0),
            };
            headerLabels.Add(colHeader);
            // A transparent background makes the whole header cell clickable, not just the glyphs.
            var headerCell = new Border { Background = Brushes.Transparent, Child = colHeader, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            ToolTip.SetTip(headerCell, Loc.S("Click to sort"));
            headerCell.PointerPressed += (_, _) => SortBy(columnIndex);
            Grid.SetColumn(headerCell, i);
            headerGrid.Children.Add(headerCell);
        }

        // ListBoxItem has Padding(8,4,8,4); row Margin(4,0,16,0) matches header Padding(12,6,24,6) and clears the vertical ScrollBar.
        ListBox? listBox = null;
        listBox = new ListBox
        {
            Background = Brush("#151926"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            SelectionMode = SelectionMode.Single,
            ItemsSource = table.Rows,
            ItemTemplate = new FuncDataTemplate<ClubListRowModel>((row, _) =>
            {
                if (row is null) return null;
                var grid = new Grid
                {
                    ColumnDefinitions = CreateClubListColumns(table.Columns),
                    Margin = new Thickness(4, 0, 16, 0),
                };
                grid.DoubleTapped += (_, e) =>
                {
                    e.Handled = true;
                    if (listBox?.SelectedIndex is int idx && idx >= 0 && idx < order.Length)
                        onRowDoubleClicked?.Invoke(order[idx]);
                };
                Avalonia.Automation.AutomationProperties.SetName(grid, row.AutomationName);
                int count = Math.Min(table.Columns.Count, row.Cells.Count);
                for (int i = 0; i < count; i++)
                {
                    var cell = row.Cells[i];
                    var cellView = BuildClubListCellView(cell, i == 0 ? 0 : 16, ShouldTranslateClubListValue(table.Columns[i].Header));
                    Grid.SetColumn(cellView, i);
                    grid.Children.Add(cellView);
                }
                return grid;
            }),
        };
        sortedList = listBox;
        listBox.SelectedIndex = selectedIdx;
        listBox.SelectionChanged += (_, _) =>
        {
            if (clubUpdating) return;
            int idx = listBox.SelectedIndex;
            if (idx >= 0 && idx < order.Length)
                onRowSelected(order[idx]);
        };
        listBox.DoubleTapped += (_, e) =>
        {
            if (UiKit.OnRow(e) && listBox.SelectedIndex >= 0 && listBox.SelectedIndex < order.Length)
                onRowDoubleClicked?.Invoke(order[listBox.SelectedIndex]);
        };

        return new Border
        {
            Background = UiKit.Surface,
            BorderBrush = UiKit.BorderSubtle,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new Border
                    {
                        Background = UiKit.SurfaceHeader,
                        BorderBrush = UiKit.BorderSubtle,
                        BorderThickness = new Thickness(0, 0, 0, 1),
                        Padding = new Thickness(12, 6, 24, 6),
                        Child = headerGrid,
                    },
                    listBox,
                },
            },
        };
    }

    static bool ShouldTranslateClubListValue(string key) =>
        key is "Type" or "Source" or "For Comp." or "Income Type" or "Kit" or "Status" or "Reason" or "Kind" or "Permanent";

    static string TranslateClubListValue(string text)
    {
        if (text.StartsWith("Unknown (", StringComparison.Ordinal) && text.EndsWith(')'))
            return Loc.F("Unknown ({0})", text[9..^1]);
        if (text.StartsWith("Type ", StringComparison.Ordinal))
            return Loc.F("Type {0}", text[5..]);
        if (text.StartsWith("Role ", StringComparison.Ordinal))
            return Loc.F("Role {0}", text[5..]);
        if (text.StartsWith("Other (", StringComparison.Ordinal) && text.EndsWith(')'))
            return Loc.F("Other ({0})", text[7..^1]);
        return Loc.S(text);
    }

    Control BuildClubListCellView(ClubListCell cell, double leftMargin, bool translateValue)
    {
        string displayText = translateValue || cell.Text is "True" or "False" or "None" or "Yes" or "No"
            ? TranslateClubListValue(cell.Text)
            : cell.Text;
        if (cell.SwatchArgb is { } argb)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(leftMargin, 0, 0, 0),
                Children =
                {
                    new Border
                    {
                        Width = 14,
                        Height = 14,
                        BorderBrush = Brush("#282E42"),
                        BorderThickness = new Thickness(1),
                        Background = SwatchBrush(argb),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = displayText,
                        FontSize = 13,
                        Foreground = Brush("#F1F5F9"),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            };
        }

        return new TextBlock
        {
            Text = displayText,
            FontSize = 13,
            Foreground = Brush("#F1F5F9"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = cell.RightAlign ? TextAlignment.Right : TextAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(leftMargin, 0, 0, 0),
        };
    }

    /// <summary>Table plus its detail card, spaced the same way as the form sections on every entity screen.</summary>
    void AddClubListSection(Control table, Control detail)
    {
        clubEditorPanel.Children.Add(new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { table, detail },
        });
    }

    Control? BuildClubListDetailCard(ClubListRowModel row)
    {
        if (row.Details.Count == 0)
            return null;

        var panel = new StackPanel { Spacing = 0 };

        int rowIndex = 0;
        foreach (var field in row.Details)
        {
            string displayValue = field.BoolValue is not null || ShouldTranslateClubListValue(field.Label) || field.Value is "True" or "False" or "None" or "Yes" or "No"
                ? TranslateClubListValue(field.Value)
                : field.Value;
            var line = new DockPanel { Height = 28, LastChildFill = true };
            Control rightView = field.SwatchArgb is { } argb
                ? new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new Border
                        {
                            Width = 14,
                            Height = 14,
                            BorderBrush = Brush("#282E42"),
                            BorderThickness = new Thickness(1),
                            Background = SwatchBrush(argb),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = displayValue,
                            FontSize = 13,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brush("#F8FAFC"),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    },
                }
                : new TextBlock
                {
                    Text = displayValue,
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Brush("#F8FAFC"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Right,
                };

            DockPanel.SetDock(rightView, Dock.Right);
            line.Children.Add(rightView);
            line.Children.Add(new TextBlock
            {
                Text = Loc.ClubField(field.Label),
                FontSize = 13,
                Foreground = Brush("#CBD5E1"),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0),
            });

            panel.Children.Add(new Border
            {
                Background = rowIndex++ % 2 == 1 ? UiKit.SurfaceZebra : Brushes.Transparent,
                Padding = new Thickness(12, 2, 12, 2),
                Child = line,
            });
        }

        var detailCard = UiKit.Card("Details", panel);
        detailCard.MaxWidth = 540;
        detailCard.HorizontalAlignment = HorizontalAlignment.Left;
        return detailCard;
    }

    internal static ColumnDefinitions CreateClubListColumns(IReadOnlyList<ClubListColumn> columns)
    {
        var defs = new ColumnDefinitions();
        foreach (var col in columns)
            defs.Add(new ColumnDefinition(new GridLength(col.Width, col.IsStar ? GridUnitType.Star : GridUnitType.Pixel)));
        return defs;
    }

    Control BuildClubGroupCard(VisibleClubGroup group, ClubDraft draft)
    {
        var panel = new StackPanel { Spacing = 0 };

        TextBlock? distributionTotalText = null;
        var distFields = group.Title == "Distribution"
            ? group.Fields.Where(f => ClubSectionLayout.SupporterDistributionFields.Contains(f.Field)).ToArray()
            : [];

        void RefreshDistributionTotal()
        {
            if (distributionTotalText is null) return;
            long sum = distFields.Sum(f => draft.Value(f.Target));
            distributionTotalText.Text = $"{sum.ToString(Inv)}%";
            distributionTotalText.Foreground = Brush(sum is 100 or 0 ? "#F1F5F9" : "#F59E0B");
        }

        int rowIndex = 0;
        foreach (var field in group.Fields)
        {
            var rowControl = BuildClubFieldRow(field, draft, RefreshDistributionTotal);
            panel.Children.Add(new Border
            {
                Background = rowIndex++ % 2 == 1 ? UiKit.SurfaceZebra : Brushes.Transparent,
                Padding = new Thickness(10, 2, 10, 2),
                Child = rowControl,
            });
        }

        if (distFields.Length == ClubSectionLayout.SupporterDistributionFields.Count)
        {
            distributionTotalText = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
            };
            RefreshDistributionTotal();

            var totalLine = new DockPanel { Height = 28, LastChildFill = true };
            DockPanel.SetDock(distributionTotalText, Dock.Right);
            totalLine.Children.Add(distributionTotalText);
            totalLine.Children.Add(new TextBlock
            {
                Text = Loc.S("Total"),
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#94A3B8"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            panel.Children.Add(new Border
            {
                BorderBrush = UiKit.BorderSubtle,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(10, 2, 10, 2),
                Child = totalLine,
            });
        }

        return UiKit.Card(group.Title, panel);
    }

    Control BuildClubFieldRow(VisibleClubField field, ClubDraft draft, Action? onChanged)
    {
        var line = new DockPanel { MinHeight = 29, LastChildFill = true };
        var label = new TextBlock
        {
            Text = Loc.ClubField(field.Meta.Label),
            FontSize = 13,
            Foreground = Brush("#CBD5E1"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
        };

        var target = field.Target;
        long currentVal = draft.Value(target);

        void StageValid(long val)
        {
            draft.Stage(field, val);
            onChanged?.Invoke();
            UpdateClubButtonsAndStatus(draft);
        }

        void MarkInvalid(bool invalid)
        {
            draft.SetInvalid(target, invalid);
            UpdateClubButtonsAndStatus(draft);
        }

        if (field.Kind == ClubDisplayKind.Bool)
        {
            var check = new CheckBox
            {
                IsChecked = currentVal != 0,
                MinWidth = 0,
                Padding = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            check.IsCheckedChanged += (_, _) =>
            {
                if (!clubUpdating)
                    StageValid(check.IsChecked == true ? 1 : 0);
            };
            DockPanel.SetDock(check, Dock.Right);
            line.Children.Add(check);
            line.Children.Add(label);
            return line;
        }

        if (field.Kind == ClubDisplayKind.Date)
        {
            var box = new DateBox
            {
                Text = ClubFieldInfo.FormatValue(currentVal, ClubDisplayKind.Date),
                Width = 106,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Background = Brush("#0E1018"),
                BorderBrush = Brush("#282E42"),
                Foreground = Brush("#F8FAFC"),
            };
            box.TextChanged += (_, _) =>
            {
                if (clubUpdating) return;
                bool ok = ClubFieldInfo.TryParseDate(box.Text, out long parsed);
                box.BorderBrush = Brush(ok ? "#282E42" : "#B91C1C");
                if (ok) StageValid(parsed);
                else MarkInvalid(true);
            };
            DockPanel.SetDock(box, Dock.Right);
            line.Children.Add(box);
            line.Children.Add(label);
            return line;
        }

        if (field.Kind == ClubDisplayKind.Colour)
        {
            var box = new TextBox
            {
                Text = ClubFieldInfo.FormatValue(currentVal, ClubDisplayKind.Colour),
                Width = 106,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brush("#0E1018"),
                BorderBrush = Brush("#282E42"),
                Foreground = Brush("#F8FAFC"),
            };
            long originalVal = draft.OriginalValue(target);
            box.TextChanged += (_, _) =>
            {
                if (clubUpdating) return;
                bool ok = ClubFieldInfo.TryParseColour(box.Text, originalVal, out long parsed);
                box.BorderBrush = Brush(ok ? "#282E42" : "#B91C1C");
                if (ok) StageValid(parsed);
                else MarkInvalid(true);
            };
            var colourPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { ColourInput(box) },
            };
            DockPanel.SetDock(colourPanel, Dock.Right);
            line.Children.Add(colourPanel);
            line.Children.Add(label);
            return line;
        }

        if (field.Meta.Options is { Count: > 0 })
        {
            long originalVal = draft.OriginalValue(target);
            var items = ClubFieldInfo.OptionsFor(field.Meta, originalVal, currentVal);
            var combo = new ComboBox
            {
                Width = 176,
                FontSize = 13,
                ItemsSource = items,
                SelectedItem = items.FirstOrDefault(i => i.Value == currentVal),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (clubUpdating) return;
                if (combo.SelectedItem is ClubCodeOption chosen)
                    StageValid(chosen.Value);
            };
            DockPanel.SetDock(combo, Dock.Right);
            line.Children.Add(combo);
            line.Children.Add(label);
            return line;
        }

        double inputWidth = field.Kind switch
        {
            ClubDisplayKind.Rating20 or ClubDisplayKind.Percent or ClubDisplayKind.Rating100 => 56,
            ClubDisplayKind.PlainInt => 78,
            ClubDisplayKind.FormattedInt => 118,
            _ => 136,
        };
        string format = field.Kind is ClubDisplayKind.FormattedInt or ClubDisplayKind.Money ? "N0" : "0";
        decimal increment = field.Kind == ClubDisplayKind.Money ? 10_000 : 1;
        // Money is stored in pounds; the box shows the player's chosen currency like the game does.
        bool money = field.Kind == ClubDisplayKind.Money;
        decimal moneyRate = money ? Fm27Editor.Core.Settings.CurrencySetting.RatePerGbp : 1m;
        decimal Show(long pounds) => Math.Round(pounds * moneyRate, 0, MidpointRounding.AwayFromZero);
        long Hide(decimal display) => (long)Math.Round(display / moneyRate, 0, MidpointRounding.AwayFromZero);

        // -1 is the game's "not set" (4,955 of 5,251 clubs have no Social Media Followers); an empty box says so, a number would lie.
        bool notSet = currentVal == -1 && field.Min >= -1;
        var numeric = new NumericUpDown
        {
            Minimum = money ? Show(field.Min) : field.Min,
            Maximum = money ? Show(field.Max) : field.Max,
            Increment = money ? Math.Max(1m, Math.Round(increment * moneyRate)) : increment,
            Value = notSet ? null : money ? Show(currentVal) : currentVal,
            Watermark = notSet ? Loc.S("None") : null,
            FormatString = format,
            ShowButtonSpinner = false,
            Width = inputWidth,
            FontSize = 13,
            FontWeight = FontWeight.Bold,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            Background = Brush("#0E1018"),
            BorderBrush = Brush("#282E42"),
            Foreground = field.Kind == ClubDisplayKind.Rating20
                ? RatingColor((int)Math.Clamp(currentVal, 1, 20))
                : Brush("#F8FAFC"),
        };
        if (money) numeric.TextConverter = MoneyTextConverter.Instance;
        numeric.ValueChanged += (_, e) =>
        {
            if (clubUpdating || e.OldValue == e.NewValue) return;
            if (e.NewValue is null)
            {
                numeric.BorderBrush = Brush("#B91C1C");
                MarkInvalid(true);
                return;
            }
            numeric.BorderBrush = Brush("#282E42");
            // A money box set back to the amount it showed keeps the stored pounds exactly: in a currency worth more than
            // a pound (KWD) several pound values share one displayed number, and converting back must not move them.
            long value = money ? Fm27Editor.Core.Settings.CurrencySetting.FromDisplay(e.NewValue.Value, currentVal) : (long)Math.Round(e.NewValue.Value);
            value = Math.Clamp(value, field.Min, field.Max);
            StageValid(value);
            if (field.Kind == ClubDisplayKind.Rating20)
                numeric.Foreground = RatingColor((int)Math.Clamp(value, 1, 20));
        };

        Control rightControl = field.Kind == ClubDisplayKind.Percent
            ? new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    numeric,
                    new TextBlock
                    {
                        Text = "%",
                        Width = 12,
                        FontSize = 13.5,
                        Foreground = Brush("#94A3B8"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            }
            : numeric;
        if (money)
        {
            rightControl = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 3,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = Fm27Editor.Core.Settings.CurrencySetting.Current.Symbol,
                        FontSize = 13,
                        Foreground = Brush("#94A3B8"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    numeric,
                },
            };
        }

        DockPanel.SetDock(rightControl, Dock.Right);
        line.Children.Add(rightControl);
        line.Children.Add(label);
        return line;
    }

    static IBrush SwatchBrush(long raw)
    {
        var (a, r, g, b) = ClubFieldInfo.SwatchArgb(raw);
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

    static string ClubUserMessage(Exception error) => error switch
    {
        StaleObjectException => Loc.S("Club data changed in game; reloaded"),
        _ => UserMessage.For(error),
    };

    int SaveClubAllowances(GameSession session, ClubEntry club, ClubAllowanceDraft allow)
    {
        if (allow.Invalid.Count > 0)
            throw new InvalidOperationException(Loc.S("Enter valid extra staff allowance numbers within their range."));
        if (allow.Pending.Count == 0)
        {
            clubAllowanceDrafts.Remove(club.Address);
            return 0;
        }

        var snapshot = allow.Snapshot ?? session.StaffAllowances.ReadForClub(club.Address)
            ?? throw new InvalidOperationException(Loc.S("Extra staff allowances are available only for human-managed clubs."));

        int saved = 0;
        foreach (var (field, value) in allow.Pending.OrderBy(kv => kv.Key))
        {
            int clamped = ClubAdditionalStaffCard.ClampValue(value, StaffAllowanceAccess.MaxValue(field));
            backend.Edits.Execute("Club", club.DisplayName, StaffAllowanceAccess.Label(field), () =>
                session.StaffAllowances.Set(snapshot, field, clamped));
            saved++;
        }
        clubAllowanceDrafts.Remove(club.Address);
        return saved;
    }

    Control BuildClubStaffAllowancesCard(ClubEntry club, StaffAllowanceSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            clubAllowanceDrafts.Remove(club.Address);
            return ClubAdditionalStaffCard.Build(
                null,
                _ => null,
                (_, _) => { },
                (_, _) => { },
                () => clubUpdating);
        }

        if (!clubAllowanceDrafts.TryGetValue(club.Address, out var allowDraft))
            clubAllowanceDrafts[club.Address] = allowDraft = new();
        allowDraft.Snapshot = snapshot;
        allowDraft.Invalid.Clear();

        foreach (var field in StaffAllowanceAccess.AllFields)
        {
            if (allowDraft.Pending.TryGetValue(field, out int staged) && staged == snapshot[field])
                allowDraft.Pending.Remove(field);
        }

        return ClubAdditionalStaffCard.Build(
            snapshot,
            field => allowDraft.Pending.TryGetValue(field, out int val) ? val : null,
            (field, staged) =>
            {
                if (staged is { } val)
                    allowDraft.Pending[field] = val;
                else
                    allowDraft.Pending.Remove(field);
                UpdateClubStatusFromDraft();
            },
            (field, isInvalid) =>
            {
                if (isInvalid)
                    allowDraft.Invalid.Add(field);
                else
                    allowDraft.Invalid.Remove(field);
                UpdateClubStatusFromDraft();
            },
            () => clubUpdating);
    }
}
