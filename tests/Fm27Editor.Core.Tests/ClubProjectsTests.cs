using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class ClubProjectsTests
{
    [Theory]
    [InlineData("27.1.0+2505117-mac.json", 0x5785440u, 0x56C2070u)]
    [InlineData("27.1.0+2505719-touch-mac.json", 0x592DF00u, 0x58679F0u)]
    public void Reads_announced_ground_projects_before_the_finance_queue_and_prefers_queued_expansion(
        string build, uint financeRoot, uint financeVtable)
    {
        var installed = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var profile = OffsetProfile.Load(installed.ToJson());
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase, clubAddress = 0x6200_0000, finance = 0x6201_0000;
        const ulong team = 0x6202_0000, teamSlots = 0x6203_0000, details = 0x6204_0000, holder = 0x6205_0000;
        const ulong requests = 0x6206_0000, requestSlots = 0x6207_0000, expansions = 0x6208_0000;
        var club = new ClubEntry(clubAddress, 680, "Manchester United", null, 0, []);
        var clubBytes = memory.Map(clubAddress, 0x80);
        TestWorld.Set64(clubBytes, 0, module + profile.Club!.ClubVtableRva);
        BitConverter.GetBytes(680).CopyTo(clubBytes, profile.Club.Id!.Value);
        TestWorld.Set64(clubBytes, 0x18, teamSlots);
        TestWorld.Set64(clubBytes, 0x20, teamSlots + 8);
        TestWorld.Set64(clubBytes, 0x28, teamSlots + 8);
        TestWorld.Set64(memory.Map(teamSlots, 8), 0, team);
        var teamBytes = memory.Map(team, 0xA8);
        TestWorld.Set64(teamBytes, 0, module + profile.Team!.VtableRva);
        TestWorld.Set64(teamBytes, 0xA0, details);
        TestWorld.Set64(memory.Map(details, 0xD0), 0xC8, holder);
        var holderBytes = memory.Map(holder, 0x80);
        TestWorld.Set64(holderBytes, 0, module + profile.Club.Lists!.VisionHolderVtable);
        holderBytes[0x74] = 4;
        TestWorld.Set64(holderBytes, 0x78, requests);

        var board = memory.Map(requests, 0x30);
        TestWorld.Set64(board, 0x18, requestSlots);
        TestWorld.Set64(board, 0x20, requestSlots + 32);
        TestWorld.Set64(board, 0x28, requestSlots + 32);
        var slots = memory.Map(requestSlots, 32);
        MapRequest(0, 3000, 0x07EB12C4, 0x076C0001, 0); // 15 July 2027, 196th day (plus flags)
        MapRequest(1, 3001, 0x07EC00A7, 0x076C0001, 0); // 15 June 2028, 167th day
        MapRequest(2, 3000, 0x07ED0001, 0x07EA014E, 0); // already closed
        MapRequest(3, 3001, 0x07ED0001, 0x076C0001, 1); // inactive

        TestWorld.Set64(memory.Map(module + financeRoot, 8), 0, finance);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(manager, 0, module + financeVtable);
        TestWorld.Set64(manager, 0x18, expansions);
        var expansionQueue = memory.Map(expansions, 24);
        TestWorld.Set64(manager, 0x20, 0x6209_0000);
        memory.Map(0x6209_0000, 24);
        TestWorld.Set64(manager, 0x28, 0x620A_0000);
        memory.Map(0x620A_0000, 24);
        MapClock(profile.GameDate!.Primary, 0x6220_0000);
        if (profile.GameDate.CrossCheck is { } crossCheck) MapClock(crossCheck, 0x6221_0000);

        var reader = new ClubProjects(memory, profile, module);
        var before = reader.ReadOverview(club);
        Assert.Empty(before.Scheduled);
        Assert.Equal(new DateOnly(2027, 7, 15), before.AnnouncedExpansionDate);
        Assert.Equal(new StadiumPlan(null, new DateOnly(2028, 6, 15)), before.Planning);

        const ulong queuedSlots = 0x6222_0000, queued = 0x6223_0000, stadium = 0x6224_0000;
        TestWorld.Set64(expansionQueue, 0, queuedSlots);
        TestWorld.Set64(expansionQueue, 8, queuedSlots + 8);
        TestWorld.Set64(expansionQueue, 16, queuedSlots + 8);
        TestWorld.Set64(memory.Map(queuedSlots, 8), 0, queued);
        var queuedRecord = memory.Map(queued, 0x20);
        TestWorld.Set64(queuedRecord, 0, clubAddress);
        TestWorld.Set64(queuedRecord, 8, stadium);
        TestWorld.Set64(memory.Map(stadium, 8), 0, module + profile.Stadium!.VtableRva);
        BitConverter.GetBytes(0x07EB00C4u).CopyTo(queuedRecord, 0x18);
        var after = reader.ReadOverview(club);
        Assert.Null(after.AnnouncedExpansionDate);
        Assert.Equal(ClubProjectKind.StadiumExpansion, Assert.Single(after.Scheduled).Kind);

        void MapRequest(int index, ushort kind, uint target, uint completed, byte flags)
        {
            ulong address = 0x6210_0000 + (ulong)index * 0x1000;
            TestWorld.Set64(slots, index * 8, address);
            var row = memory.Map(address, 0x148);
            BitConverter.GetBytes(target).CopyTo(row, 0x12C);
            BitConverter.GetBytes(completed).CopyTo(row, 0x130);
            BitConverter.GetBytes(kind).CopyTo(row, 0x13C);
            row[0x140] = flags;
        }

        void MapClock(DateChain chain, ulong address)
        {
            TestWorld.Set64(memory.Map(module + chain.RootRva, 8), 0, address);
            var date = memory.Map(address, chain.DateOffset + 4);
            TestWorld.Set64(date, 0, module + chain.ObjectVtableRva!.Value);
            BitConverter.GetBytes(0x07EA014Eu).CopyTo(date, chain.DateOffset); // 30 November 2026
        }
    }

    [Theory]
    [InlineData("27.1.0+2505117-mac.json", 0x5785440u, 0x56C2070u, 0x566A870u)]
    [InlineData("27.1.0+2505719-touch-mac.json", 0x592DF00u, 0x58679F0u, 0x58101E8u)]
    public void Moves_only_the_reviewed_project_to_today_and_refuses_undo_after_the_queue_consumes_it(
        string build, uint financeRoot, uint financeVtable, uint upgradeVtable)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase;
        const ulong clubAddress = 0x6000_0000, finance = 0x6001_0000, upgrades = 0x6002_0000;
        const ulong rows = 0x6003_0000, upgrade = 0x6004_0000, other = 0x6005_0000;
        const ulong plans = 0x6007_0000, planSlots = 0x6008_0000, plan = 0x6009_0000, closed = 0x600A_0000;
        var club = new ClubEntry(clubAddress, 20, "Test Club", null, 0, []);
        var clubBytes = memory.Map(clubAddress, 0x80);
        TestWorld.Set64(clubBytes, 0, module + profile.Club!.ClubVtableRva);
        BitConverter.GetBytes(20).CopyTo(clubBytes, profile.Club.Id!.Value);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(memory.Map(module + financeRoot, 8), 0, finance);
        TestWorld.Set64(manager, 0, module + financeVtable);
        TestWorld.Set64(manager, 0x18, 0x6006_0000);
        memory.Map(0x6006_0000, 24);
        TestWorld.Set64(manager, 0x20, upgrades);
        var queue = memory.Map(upgrades, 24);
        TestWorld.Set64(queue, 0, rows);
        TestWorld.Set64(queue, 8, rows + 16);
        TestWorld.Set64(queue, 16, rows + 16);
        var table = memory.Map(rows, 16);
        TestWorld.Set64(table, 0, upgrade);
        TestWorld.Set64(table, 8, other);
        const uint from = 0x07EB3240, to = 0x07EA3348; // game date: 24 Nov 2026, 328th day of the year
        var facility = memory.Map(upgrade, 40);
        TestWorld.Set64(facility, 0, module + upgradeVtable);
        TestWorld.Set64(facility, 8, clubAddress);
        BitConverter.GetBytes(from).CopyTo(facility, 0x10);
        var unrelated = memory.Map(other, 40);
        TestWorld.Set64(unrelated, 8, clubAddress + 0x100);
        BitConverter.GetBytes(from).CopyTo(unrelated, 0x10);
        var today = new DateOnly(2026, 11, 24);
        MapClock(profile.GameDate!.Primary, 0x6100_0000);
        if (profile.GameDate.CrossCheck is { } crossCheck) MapClock(crossCheck, 0x6101_0000);

        TestWorld.Set64(manager, 0x28, plans);
        var planningQueue = memory.Map(plans, 24);
        TestWorld.Set64(planningQueue, 0, planSlots);
        TestWorld.Set64(planningQueue, 8, planSlots + 16);
        TestWorld.Set64(planningQueue, 16, planSlots + 16);
        var planningTable = memory.Map(planSlots, 16);
        TestWorld.Set64(planningTable, 0, closed);
        TestWorld.Set64(planningTable, 8, plan);
        var closedPlan = memory.Map(closed, 0x40);
        TestWorld.Set64(closedPlan, 0, clubAddress);
        BitConverter.GetBytes(0x076C0201u).CopyTo(closedPlan, 0x38);
        var activePlan = memory.Map(plan, 0x40);
        TestWorld.Set64(activePlan, 0, clubAddress);
        const uint expiry = 0x07EC123C; // 29 Feb 2028, 60th day of the year (plus flags)
        BitConverter.GetBytes(expiry).CopyTo(activePlan, 0x38);

        var editor = new ClubProjects(memory, profile, module);
        var overview = editor.ReadOverview(club);
        Assert.Equal(today, overview.Today);
        Assert.Equal(new DateOnly(2028, 2, 29), Assert.IsType<StadiumPlan>(overview.Planning).ExpiryDate);
        var project = Assert.Single(overview.Scheduled);
        Assert.Equal(new DateOnly(2027, 3, 5), project.ScheduledDate);
        Assert.Equal(ClubProjectKind.FacilityUpgrade, project.Kind);
        var roundTrip = OffsetProfile.Load(profile.ToJson());
        Assert.Equal(overview.Planning, new ClubProjects(memory, roundTrip, module).ReadOverview(club).Planning);

        // Make the second row a project of the same club, then change it after review.
        TestWorld.Set64(unrelated, 0, module + upgradeVtable);
        TestWorld.Set64(unrelated, 8, clubAddress);
        var reviewed = editor.Read(club);
        Assert.Equal(2, reviewed.Count);
        const uint changed = 0x07EB3241;
        BitConverter.GetBytes(changed).CopyTo(unrelated, 0x10);
        Assert.Throws<Fm27Editor.Core.Players.StaleObjectException>(() => editor.BringForward(club, reviewed, today));
        Assert.Equal(from, BitConverter.ToUInt32(facility, 0x10));
        Assert.Equal(changed, BitConverter.ToUInt32(unrelated, 0x10));
        TestWorld.Set64(unrelated, 8, clubAddress + 0x100);
        BitConverter.GetBytes(from).CopyTo(unrelated, 0x10);

        editor.BringForward(club, project, today);
        Assert.Equal(to, BitConverter.ToUInt32(facility, 0x10));
        Assert.Equal(from, BitConverter.ToUInt32(unrelated, 0x10));
        Assert.Equal(expiry, BitConverter.ToUInt32(activePlan, 0x38));
        TestWorld.Set64(planningQueue, 8, planSlots + 8);
        Assert.Null(editor.ReadOverview(club).Planning);
        var due = Assert.Single(editor.Read(club));
        Assert.Equal(today, due.ScheduledDate);
        Assert.Throws<Fm27Editor.Core.Players.StaleObjectException>(() => editor.BringForward(club, due, today));

        var journal = new ChangeJournal();
        using (journal.Begin("Club", club.DisplayName, "Finish Club Projects", (_, _) => new ChangeLabel("Scheduled completion",
            Kind: ChangeValueKind.Date, ValidateUndo: staged => editor.ValidateUndo(staged, club, project, today))))
            journal.Record(upgrade + 0x10, BitConverter.GetBytes(from), BitConverter.GetBytes(to));
        var entry = Assert.Single(Assert.Single(journal.Groups).Entries);
        TestWorld.Set64(queue, 8, rows); // Game consumed the row, but its old heap bytes still match.
        Assert.False(journal.Undo(entry, memory, out _));
        Assert.Equal(to, BitConverter.ToUInt32(facility, 0x10));

        void MapClock(DateChain chain, ulong address)
        {
            TestWorld.Set64(memory.Map(module + chain.RootRva, 8), 0, address);
            var dateObject = memory.Map(address, chain.DateOffset + 4);
            TestWorld.Set64(dateObject, 0, module + chain.ObjectVtableRva!.Value);
            BitConverter.GetBytes(0x07EA0148u).CopyTo(dateObject, chain.DateOffset);
        }
    }

    /// <summary>
    /// Windows Steam 27.1.0 (19/12/2026): Bournemouth's expansion queue row (+0x18 of the finance manager's vector) and
    /// its db::STADIUM_CHANGE (Vitality Stadium at +0x20, completion 30/06/2027 at +0x34) both carried 0x07EB00B5.
    /// Moving both to the game date made the game finish the works the next day (capacity 11,700 → 17,358); the
    /// 30/06/2028 change of the same stadium stayed scheduled. A change whose queue row FM already dropped (Wrexham)
    /// is still a project of the club that owns the stadium.
    /// </summary>
    [Fact]
    public void Brings_the_stadium_change_of_the_same_stadium_and_day_forward_with_its_expansion_on_windows()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        var layout = profile.Club!.Projects!;
        var changesLayout = layout.StadiumChanges!;
        var stadiumLayout = profile.Stadium!;
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase;
        const ulong clubAddress = 0x6000_0000, finance = 0x6001_0000, expansions = 0x6002_0000, expansionSlots = 0x6003_0000;
        const ulong expansion = 0x6004_0000, empty = 0x6005_0000, table = 0x6006_0000, changeVector = 0x6007_0000;
        const ulong changeSlots = 0x6008_0000, change2027 = 0x6009_0000, change2028 = 0x600A_0000, venue = 0x600B_0000;
        const ulong orphan = 0x600C_0000, ownVenue2 = 0x600D_0000;
        var club = new ClubEntry(clubAddress, 20, "Bournemouth", null, 0, []);
        var clubBytes = memory.Map(clubAddress, 0x80);
        TestWorld.Set64(clubBytes, 0, module + profile.Club.ClubVtableRva);
        BitConverter.GetBytes(20).CopyTo(clubBytes, profile.Club.Id!.Value);
        TestWorld.Set64(memory.Map(module + layout.ManagerRoot, 8), 0, finance);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(manager, 0, module + layout.ManagerVtable);
        memory.Map(empty, 24);
        TestWorld.Set64(manager, layout.Upgrades.Vector, empty);
        TestWorld.Set64(manager, layout.StadiumPlans!.Value, empty);
        TestWorld.Set64(manager, layout.Expansions.Vector, expansions);
        Vector(expansions, expansionSlots, expansion);
        const uint june2027 = 0x07EB00B5, june2028 = 0x07EC00B6, july2027 = 0x07EB00B6, today = 0x07EA0162;
        var row = memory.Map(expansion, 0x40);
        TestWorld.Set64(row, layout.Expansions.Club, clubAddress);
        TestWorld.Set64(row, layout.Expansions.Stadium!.Value, venue);
        BitConverter.GetBytes(june2027).CopyTo(row, layout.Expansions.Date);
        Venue(venue);
        Venue(ownVenue2);
        TestWorld.Set64(memory.Map(module + changesLayout.TableRoot, 8), 0, table);
        var tableBytes = memory.Map(table, changesLayout.Vector + 8);
        TestWorld.Set64(tableBytes, 0, module + changesLayout.TableVtable);
        TestWorld.Set64(tableBytes, changesLayout.Vector, changeVector);
        Vector(changeVector, changeSlots, change2027, change2028, orphan);
        var first = Change(change2027, venue, june2027);
        var second = Change(change2028, venue, june2028);
        var dropped = Change(orphan, ownVenue2, july2027);
        var clock = profile.GameDate!.Primary;
        TestWorld.Set64(memory.Map(module + clock.RootRva, 8), 0, 0x6100_0000);
        var dateObject = memory.Map(0x6100_0000, clock.DateOffset + 4);
        TestWorld.Set64(dateObject, 0, module + clock.ObjectVtableRva!.Value);
        BitConverter.GetBytes(today).CopyTo(dateObject, clock.DateOffset);

        var editor = new ClubProjects(memory, profile, module);
        var scheduled = editor.ReadOverview(club).Scheduled;
        Assert.Equal(3, scheduled.Count);
        var withQueue = Assert.Single(scheduled, p => p.DateAddress != 0);
        Assert.Equal(new DateOnly(2027, 6, 30), withQueue.ScheduledDate);
        Assert.Equal(change2027 + (ulong)changesLayout.Date, withQueue.ChangeDateAddress);
        Assert.Equal(new DateOnly(2027, 7, 1), Assert.Single(scheduled, p => p.Address == orphan).ScheduledDate);

        editor.BringForward(club, withQueue, editor.ReadOverview(club).Today);
        Assert.Equal(today, BitConverter.ToUInt32(row, layout.Expansions.Date));
        Assert.Equal(today, BitConverter.ToUInt32(first, changesLayout.Date));
        Assert.Equal(june2028, BitConverter.ToUInt32(second, changesLayout.Date));
        Assert.Equal(july2027, BitConverter.ToUInt32(dropped, changesLayout.Date));

        void Vector(ulong header, ulong slots, params ulong[] items)
        {
            var h = memory.Map(header, 24);
            TestWorld.Set64(h, 0, slots);
            TestWorld.Set64(h, 8, slots + (ulong)items.Length * 8);
            TestWorld.Set64(h, 16, slots + (ulong)items.Length * 8);
            var s = memory.Map(slots, items.Length * 8);
            for (int i = 0; i < items.Length; i++) TestWorld.Set64(s, i * 8, items[i]);
        }

        void Venue(ulong address)
        {
            var bytes = memory.Map(address, stadiumLayout.Owner + 8);
            TestWorld.Set64(bytes, 0, module + stadiumLayout.VtableRva);
            TestWorld.Set64(bytes, stadiumLayout.Owner, clubAddress);
        }

        byte[] Change(ulong address, ulong stadium, uint date)
        {
            var bytes = memory.Map(address, 0x40);
            TestWorld.Set64(bytes, 0, module + changesLayout.Vtable);
            TestWorld.Set64(bytes, changesLayout.Stadium, stadium);
            BitConverter.GetBytes(date).CopyTo(bytes, changesLayout.Date);
            return bytes;
        }
    }

    [Theory]
    [InlineData("27.1.0+2505117-gamepass.json")]
    [InlineData("27.1.0+2505117-steam.json")]
    public void Finishes_an_announced_stadium_expansion_by_appending_a_row_due_today(string build)
    {
        var installed = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var profile = OffsetProfile.Load(installed.ToJson());
        var layout = profile.Club!.Projects!;
        var expansions = layout.Expansions;
        var rowShape = expansions.NewRow!;
        var requestsLayout = layout.BoardRequests!;
        var changesLayout = layout.StadiumChanges!;
        var lists = profile.Club.Lists!;
        var stadiumLayout = profile.Stadium!;
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase;
        const ulong clubAddress = 0x6200_0000, otherClub = 0x6200_1000, finance = 0x6201_0000;
        const ulong team = 0x6202_0000, teamSlots = 0x6203_0000, details = 0x6204_0000, holder = 0x6205_0000;
        const ulong requests = 0x6206_0000, requestSlots = 0x6207_0000, requestRow = 0x6208_0000;
        const ulong expansionVector = 0x6209_0000, expansionSlots = 0x620A_0000, templateRow = 0x620B_0000;
        const ulong home = 0x620C_0000, otherStadium = 0x620D_0000, empty = 0x620E_0000;
        const ulong table = 0x620F_0000, allocatedBlock = 0x6230_0000;

        var firstTeam = new TeamEntry(team, clubAddress, 0, 0, 0, true);
        var club = new ClubEntry(clubAddress, 680, "Manchester United", null, 0, [firstTeam]);
        var clubBytes = memory.Map(clubAddress, Math.Max(lists.Teams + 24, profile.Club.Id!.Value + 4));
        TestWorld.Set64(clubBytes, 0, module + profile.Club.ClubVtableRva);
        BitConverter.GetBytes(680).CopyTo(clubBytes, profile.Club.Id.Value);
        Vector(clubBytes, lists.Teams, teamSlots, team);

        var teamBytes = memory.Map(team, Math.Max(lists.TeamDetails + 8, profile.Competition!.ClubSwap!.TeamStadium + 8));
        TestWorld.Set64(teamBytes, 0, module + profile.Team!.VtableRva);
        TestWorld.Set64(teamBytes, profile.Competition.ClubSwap.TeamStadium, home);
        TestWorld.Set64(teamBytes, lists.TeamDetails, details);
        TestWorld.Set64(memory.Map(details, lists.Vision + 8), lists.Vision, holder);

        var holderBytes = memory.Map(holder, Math.Max(requestsLayout.HolderType + 1, requestsLayout.Requests + 8));
        TestWorld.Set64(holderBytes, 0, module + lists.VisionHolderVtable);
        holderBytes[requestsLayout.HolderType] = 3;
        TestWorld.Set64(holderBytes, requestsLayout.Requests, requests);

        var board = memory.Map(requests, requestsLayout.Vector + 24);
        Vector(board, requestsLayout.Vector, requestSlots, requestRow);
        var reqBytes = memory.Map(requestRow, Math.Max(requestsLayout.Flags + 1, requestsLayout.RequestType + 2));
        BitConverter.GetBytes(0x07EB00D4u).CopyTo(reqBytes, requestsLayout.TargetDate);     // 31 Jul 2027
        BitConverter.GetBytes(0x076C0001u).CopyTo(reqBytes, requestsLayout.CompletedDate);  // None
        BitConverter.GetBytes((ushort)3000).CopyTo(reqBytes, requestsLayout.RequestType);   // Expand stadium
        reqBytes[requestsLayout.Flags] = 0;

        var homeBytes = memory.Map(home, 0xC0);
        TestWorld.Set64(homeBytes, 0, module + stadiumLayout.VtableRva);
        TestWorld.Set64(homeBytes, stadiumLayout.Owner, clubAddress);
        BitConverter.GetBytes(74879).CopyTo(homeBytes, stadiumLayout.Fields[Fm27Editor.Core.Stadiums.StadiumField.Capacity].Path[^1]);
        BitConverter.GetBytes(74879).CopyTo(homeBytes, stadiumLayout.Fields[Fm27Editor.Core.Stadiums.StadiumField.SeatingCapacity].Path[^1]);
        BitConverter.GetBytes(95285).CopyTo(homeBytes, stadiumLayout.Fields[Fm27Editor.Core.Stadiums.StadiumField.ExpansionCapacity].Path[^1]);
        TestWorld.Set64(memory.Map(otherStadium, 8), 0, module + stadiumLayout.VtableRva);

        TestWorld.Set64(memory.Map(module + layout.ManagerRoot, 8), 0, finance);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(manager, 0, module + layout.ManagerVtable);
        memory.Map(empty, 24);
        TestWorld.Set64(manager, layout.Upgrades.Vector, empty);
        TestWorld.Set64(manager, layout.StadiumPlans!.Value, empty);
        TestWorld.Set64(manager, expansions.Vector, expansionVector);

        var expQueueBytes = memory.Map(expansionVector, 24);
        TestWorld.Set64(expQueueBytes, 0, expansionSlots);
        TestWorld.Set64(expQueueBytes, 8, expansionSlots + 8);
        TestWorld.Set64(expQueueBytes, 16, expansionSlots + 16); // one spare slot
        var expSlotsBytes = memory.Map(expansionSlots, 16);
        TestWorld.Set64(expSlotsBytes, 0, templateRow);

        var templateBytes = memory.Map(templateRow, rowShape.Size);
        TestWorld.Set64(templateBytes, expansions.Club, otherClub);
        TestWorld.Set64(templateBytes, expansions.Stadium!.Value, otherStadium);
        BitConverter.GetBytes(0x07EB12F4u).CopyTo(templateBytes, expansions.Date); // non-zero flags 0x1200 to verify preservation
        BitConverter.GetBytes(0x076C0001u).CopyTo(templateBytes, rowShape.StartDate);

        TestWorld.Set64(memory.Map(module + changesLayout.TableRoot, 8), 0, table);
        var tableBytes = memory.Map(table, changesLayout.Vector + 8);
        TestWorld.Set64(tableBytes, 0, module + changesLayout.TableVtable);
        TestWorld.Set64(tableBytes, changesLayout.Vector, empty);

        MapClock(profile.GameDate!.Primary, 0x6220_0000);
        if (profile.GameDate.CrossCheck is { } crossCheck) MapClock(crossCheck, 0x6221_0000);

        var projects = new ClubProjects(memory, profile, module);
        Assert.True(projects.CanFinishAnnounced);
        var before = projects.ReadOverview(club);
        var today = new DateOnly(2026, 12, 20);
        Assert.Equal(today, before.Today);
        Assert.Equal(new DateOnly(2027, 7, 31), before.AnnouncedExpansionDate);
        Assert.Empty(before.Scheduled);

        int allocatedSize = 0;
        byte[]? writtenRow = null;
        projects.FinishAnnouncedExpansion(club, today, size =>
        {
            allocatedSize = size;
            writtenRow = memory.Map(allocatedBlock, size);
            return allocatedBlock;
        });

        Assert.Equal(0x40, rowShape.Size);
        Assert.Equal(rowShape.Size, allocatedSize);
        Assert.Equal(expansionSlots + 16, BitConverter.ToUInt64(expQueueBytes, 8));
        Assert.Equal(allocatedBlock, BitConverter.ToUInt64(expSlotsBytes, 8));
        Assert.NotNull(writtenRow);
        uint expectedDate = 0x1200u | (uint)today.DayOfYear | ((uint)today.Year << 16);
        Assert.Equal(clubAddress, BitConverter.ToUInt64(writtenRow!, expansions.Club));
        Assert.Equal(home, BitConverter.ToUInt64(writtenRow!, expansions.Stadium.Value));
        Assert.Equal(expectedDate, BitConverter.ToUInt32(writtenRow!, expansions.Date));
        Assert.Equal(expectedDate, BitConverter.ToUInt32(writtenRow!, rowShape.StartDate));
        Assert.Equal(20406, BitConverter.ToInt32(writtenRow!, rowShape.AddedSeats));
        Assert.Equal(74879, BitConverter.ToInt32(writtenRow!, rowShape.Capacity));
        Assert.Equal(74879, BitConverter.ToInt32(writtenRow!, rowShape.SeatingCapacity));

        var after = projects.ReadOverview(club);
        Assert.Null(after.AnnouncedExpansionDate);
        var scheduled = Assert.Single(after.Scheduled);
        Assert.Equal(ClubProjectKind.StadiumExpansion, scheduled.Kind);
        Assert.Equal(today, scheduled.ScheduledDate);

        void Vector(byte[] owner, int vectorOffset, ulong slots, ulong item)
        {
            TestWorld.Set64(owner, vectorOffset, slots);
            TestWorld.Set64(owner, vectorOffset + 8, slots + 8);
            TestWorld.Set64(owner, vectorOffset + 16, slots + 8);
            TestWorld.Set64(memory.Map(slots, 8), 0, item);
        }

        void MapClock(DateChain chain, ulong address)
        {
            TestWorld.Set64(memory.Map(module + chain.RootRva, 8), 0, address);
            var dateObject = memory.Map(address, chain.DateOffset + 4);
            TestWorld.Set64(dateObject, 0, module + chain.ObjectVtableRva!.Value);
            BitConverter.GetBytes(0x07EA0162u).CopyTo(dateObject, chain.DateOffset); // 20 Dec 2026
        }
    }
}
