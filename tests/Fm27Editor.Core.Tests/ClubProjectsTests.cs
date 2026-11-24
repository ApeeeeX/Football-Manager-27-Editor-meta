using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class ClubProjectsTests
{
    [Theory]
    [InlineData("27.1.0+2505117-mac.json", 0x5785440u, 0x56C2070u, true)]
    [InlineData("27.1.0+2505719-touch-mac.json", 0x592DF00u, 0x58679F0u, false)]
    public void Reads_announced_ground_projects_before_the_finance_queue_and_prefers_queued_expansion(
        string build, uint financeRoot, uint financeVtable, bool createWithoutTemplate)
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
        MapEmptyStadiumChanges(memory, profile);

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

        if (createWithoutTemplate)
        {
            const ulong newRow = 0x6225_0000, home = 0x6226_0000;
            // Empty queue with one available slot, so Steam must construct its own row.
            TestWorld.Set64(expansionQueue, 8, queuedSlots);
            TestWorld.Set64(teamBytes, 0x78, home);
            var homeBytes = memory.Map(home, 0xC0);
            TestWorld.Set64(homeBytes, 0, module + profile.Stadium.VtableRva);
            BitConverter.GetBytes(74879).CopyTo(homeBytes, 0x7C);
            BitConverter.GetBytes(74879).CopyTo(homeBytes, 0x80);
            BitConverter.GetBytes(95285).CopyTo(homeBytes, 0x8C);
            var clubWithTeam = club with { Teams = [new(team, clubAddress, 0, 0, 0, true)] };
            Assert.True(reader.CanFinishAnnounced);
            reader.FinishAnnouncedExpansion(clubWithTeam, before.Today, size =>
            {
                Assert.Equal(0x40, size);
                memory.Map(newRow, size);
                return newRow;
            });

            // Steam's plain expansion record, with both dates set to 30 November 2026.
            Assert.Equal(
                "0000006200000000000026620000000000000000000000004E01EA074E01EA07B64F00000000000000000000000000007F2401007F2401000000000000000000",
                Convert.ToHexString(memory.Read(newRow, 0x40)));
            Assert.Equal(newRow, memory.ReadUInt64(queuedSlots));
            Assert.Equal(queuedSlots + 8, memory.ReadUInt64(expansions + 8));
            var due = reader.ReadOverview(clubWithTeam);
            Assert.Null(due.AnnouncedExpansionDate);
            Assert.Equal(before.Today, Assert.Single(due.Scheduled).ScheduledDate);

            // A full queue (end == capacity) moves to a larger pointer array that keeps the existing pointers.
            const ulong secondRow = 0x6227_0000, grown = 0x6228_0000, kept = 0x6229_0000;
            TestWorld.Set64(memory.Map(0x622A_0000, 8), 0, kept);
            TestWorld.Set64(expansionQueue, 0, 0x622A_0000);
            TestWorld.Set64(expansionQueue, 8, 0x622A_0008);
            TestWorld.Set64(expansionQueue, 16, 0x622A_0008);
            memory.Map(kept, 0x40);
            var sizes = new List<int>();
            reader.FinishAnnouncedExpansion(clubWithTeam, before.Today, size =>
            {
                sizes.Add(size);
                ulong at = sizes.Count == 1 ? secondRow : grown;
                memory.Map(at, size);
                return at;
            });
            Assert.Equal([0x40, 8 + 16 * 8], sizes);
            Assert.Equal(grown, memory.ReadUInt64(expansions));
            Assert.Equal(grown + 16, memory.ReadUInt64(expansions + 8));
            Assert.Equal(grown + 8 + 16 * 8, memory.ReadUInt64(expansions + 16));
            Assert.Equal(kept, memory.ReadUInt64(grown));
            Assert.Equal(secondRow, memory.ReadUInt64(grown + 8));
        }

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
        MapEmptyStadiumChanges(memory, profile);

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
    [Theory]
    [InlineData("27.1.0+2505117-steam.json", false)]
    [InlineData("27.1.0+2505117-mac.json", true)]
    public void Brings_the_pending_stadium_change_of_the_same_stadium_and_day_forward_with_its_expansion(
        string build, bool filterChanges)
    {
        var installed = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var profile = OffsetProfile.Load(installed.ToJson());
        var layout = profile.Club!.Projects!;
        var changesLayout = layout.StadiumChanges!;
        if (filterChanges)
            Assert.Equal(new StadiumChangeFilterOffsets(0x64, 2, 0x65, 8), changesLayout.Filter);
        var stadiumLayout = profile.Stadium!;
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase;
        const ulong clubAddress = 0x6000_0000, finance = 0x6001_0000, expansions = 0x6002_0000, expansionSlots = 0x6003_0000;
        const ulong expansion = 0x6004_0000, empty = 0x6005_0000, table = 0x6006_0000, changeVector = 0x6007_0000;
        const ulong changeSlots = 0x6008_0000, change2027 = 0x6009_0000, change2028 = 0x600A_0000, venue = 0x600B_0000;
        const ulong orphan = 0x600C_0000, ownVenue2 = 0x600D_0000;
        const ulong relocation = 0x600E_0000, applied = 0x600F_0000;
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
        var first = Change(change2027, venue, june2027);
        var second = Change(change2028, venue, june2028);
        var dropped = Change(orphan, ownVenue2, july2027);
        if (filterChanges)
        {
            Change(relocation, venue, june2027)[0x64] = 1;
            Change(applied, venue, june2027)[0x65] = 8;
            Vector(changeVector, changeSlots, relocation, applied, change2027, change2028, orphan);
        }
        else
            Vector(changeVector, changeSlots, change2027, change2028, orphan);
        MapClock(profile.GameDate!.Primary, 0x6100_0000);
        if (profile.GameDate.CrossCheck is { } crossCheck) MapClock(crossCheck, 0x6101_0000);

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
        if (filterChanges)
            Assert.Equal(june2027, BitConverter.ToUInt32(first, 0x30));

        void MapClock(DateChain clock, ulong address)
        {
            TestWorld.Set64(memory.Map(module + clock.RootRva, 8), 0, address);
            var dateObject = memory.Map(address, clock.DateOffset + 4);
            TestWorld.Set64(dateObject, 0, module + clock.ObjectVtableRva!.Value);
            BitConverter.GetBytes(today).CopyTo(dateObject, clock.DateOffset);
        }

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
            var bytes = memory.Map(address, 0x70);
            TestWorld.Set64(bytes, 0, module + changesLayout.Vtable);
            TestWorld.Set64(bytes, changesLayout.Stadium, stadium);
            BitConverter.GetBytes(date).CopyTo(bytes, changesLayout.Date);
            if (filterChanges)
            {
                BitConverter.GetBytes(date).CopyTo(bytes, 0x30);
                bytes[0x64] = 2;
            }
            return bytes;
        }
    }

    /// <summary>
    /// FM Touch 27.1.0, Man Utd, 1-7 January 2027: with the training upgrade's finance row (due 25-04-2027) moved to today
    /// the game upgraded the facilities, and the 3004 request (target 25-04-2027) showed Completed / "Facilities Improved"
    /// only after its target was moved to today too. The 3000 Expand ground request kept "Planning" until +0x141 bit 0 was
    /// cleared, and its outcome read "Added number#1 seats" until +0x138 (-1) held 20406, after which it read
    /// "Added 20406 seats". Request bytes below are those of the captured rows.
    /// </summary>
    [Fact]
    public void Finishing_a_project_closes_the_board_request_that_it_fulfils()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505719-touch-mac.json"));
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase, clubAddress = 0x6300_0000, finance = 0x6301_0000;
        const ulong team = 0x6302_0000, teamSlots = 0x6303_0000, details = 0x6304_0000, holder = 0x6305_0000;
        const ulong requests = 0x6306_0000, requestSlots = 0x6307_0000;
        const ulong upgrades = 0x6308_0000, upgradeSlots = 0x6309_0000, upgrade = 0x630A_0000;
        const ulong expansions = 0x630B_0000, expansionSlots = 0x630C_0000, expansion = 0x630D_0000, stadium = 0x630E_0000;
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
        TestWorld.Set64(board, 0x20, requestSlots + 24);
        TestWorld.Set64(board, 0x28, requestSlots + 24);
        var slots = memory.Map(requestSlots, 24);
        // Captured tails: +0x128 accepted, +0x12C target, +0x130/+0x134 1900, +0x138 outcome, +0x13C type, +0x140 flags.
        var expand = MapRequest(0, "4D53EA07C400EB07 01006C07 01006C07 FFFFFFFF B80B00FF 0001");  // target 15-07-2027
        var training = MapRequest(1, "6853EA077300EB07 01006C07 01006C07 FFFFFFFF BC0B00FF 0000"); // target 25-04-2027
        var other = MapRequest(2, "6853EA077300EB07 01006C07 01006C07 FFFFFFFF BD0B00FF 0001");    // same date, closed
        other[0x140] = 1;

        TestWorld.Set64(memory.Map(module + 0x592DF00, 8), 0, finance);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(manager, 0, module + 0x58679F0);
        TestWorld.Set64(manager, 0x18, expansions);
        TestWorld.Set64(manager, 0x20, upgrades);
        TestWorld.Set64(manager, 0x28, 0x630F_0000);
        memory.Map(0x630F_0000, 24);
        MapQueue(upgrades, upgradeSlots, upgrade);
        var facility = memory.Map(upgrade, 0x40);
        TestWorld.Set64(facility, 0, module + 0x58101E8);
        TestWorld.Set64(facility, 8, clubAddress);
        BitConverter.GetBytes(0x07EB0073u).CopyTo(facility, 0x10);
        MapQueue(expansions, expansionSlots, expansion);
        var works = memory.Map(expansion, 0x40);
        TestWorld.Set64(works, 0, clubAddress);
        TestWorld.Set64(works, 8, stadium);
        TestWorld.Set64(memory.Map(stadium, 8), 0, module + profile.Stadium!.VtableRva);
        BitConverter.GetBytes(0x07EB00C4u).CopyTo(works, 0x18);
        BitConverter.GetBytes(20406).CopyTo(works, 0x20);
        TestWorld.Set64(memory.Map(module + profile.GameDate!.Primary.RootRva, 8), 0, 0x6310_0000);
        var date = memory.Map(0x6310_0000, profile.GameDate.Primary.DateOffset + 4);
        TestWorld.Set64(date, 0, module + profile.GameDate.Primary.ObjectVtableRva!.Value);
        BitConverter.GetBytes(0x07EB0001u).CopyTo(date, profile.GameDate.Primary.DateOffset); // 1 January 2027
        Assert.Null(profile.GameDate.CrossCheck);
        MapEmptyStadiumChanges(memory, profile);

        var editor = new ClubProjects(memory, profile, module);
        var today = new DateOnly(2027, 1, 1);
        var scheduled = editor.Read(club);
        editor.BringForward(club, scheduled.Single(p => p.Kind == ClubProjectKind.FacilityUpgrade), today);
        Assert.Equal("6853EA070100EB07 01006C07 01006C07 FFFFFFFF BC0B00FF 0000".Replace(" ", ""), Tail(training));
        Assert.Equal("4D53EA07C400EB07 01006C07 01006C07 FFFFFFFF B80B00FF 0001".Replace(" ", ""), Tail(expand));
        Assert.Equal("6853EA077300EB07 01006C07 01006C07 FFFFFFFF BD0B00FF 0101".Replace(" ", ""), Tail(other));

        editor.BringForward(club, editor.Read(club).Single(p => p.Kind == ClubProjectKind.StadiumExpansion), today);
        Assert.Equal("4D53EA070100EB07 01006C07 01006C07 B64F0000 B80B00FF 0000".Replace(" ", ""), Tail(expand));
        var roundTrip = OffsetProfile.Load(profile.ToJson());
        Assert.Equal(profile.Club.Projects!.BoardRequests!.Close, roundTrip.Club!.Projects!.BoardRequests!.Close);

        byte[] MapRequest(int index, string tail)
        {
            ulong address = 0x6320_0000 + (ulong)index * 0x1000;
            TestWorld.Set64(slots, index * 8, address);
            var row = memory.Map(address, 0x150);
            Convert.FromHexString(tail.Replace(" ", "")).CopyTo(row, 0x128);
            return row;
        }

        static string Tail(byte[] row) => Convert.ToHexString(row, 0x128, 0x1A);

        void MapQueue(ulong header, ulong slotsAddress, ulong row)
        {
            var bytes = memory.Map(header, 24);
            TestWorld.Set64(bytes, 0, slotsAddress);
            TestWorld.Set64(bytes, 8, slotsAddress + 8);
            TestWorld.Set64(bytes, 16, slotsAddress + 8);
            TestWorld.Set64(memory.Map(slotsAddress, 8), 0, row);
        }
    }

    /// <summary>
    /// FM Touch 27.1.0, BRA, 2-5 March 2028: the new ground's construction row (expansion queue, Estádio Nabi Abi Chedid,
    /// due 01-01-2030), its kind 2 capacity change and its kind 1 move change all carried 0x07EE0001. With all three moved to
    /// the game date the game moved the team to the new ground the next day. Row bytes are those captured.
    /// </summary>
    [Fact]
    public void Finishing_a_new_ground_also_brings_forward_the_move_to_it()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505719-touch-mac.json"));
        var layout = profile.Club!.Projects!.StadiumChanges!;
        var memory = new FakeMemory();
        const ulong module = TestWorld.ModuleBase, clubAddress = 0x6400_0000, finance = 0x6401_0000;
        const ulong expansions = 0x6402_0000, slots = 0x6403_0000, works = 0x6404_0000;
        const ulong oldGround = 0x6405_0000, newGround = 0x6406_0000, team = 0x6407_0000;
        const ulong table = 0x6408_0000, header = 0x6409_0000, changeSlots = 0x640A_0000, capacity = 0x640B_0000, move = 0x640C_0000;
        var club = new ClubEntry(clubAddress, 317, "BRA", null, 0, []);
        var clubBytes = memory.Map(clubAddress, 0x80);
        TestWorld.Set64(clubBytes, 0, module + profile.Club.ClubVtableRva);
        BitConverter.GetBytes(317).CopyTo(clubBytes, profile.Club.Id!.Value);
        TestWorld.Set64(memory.Map(module + profile.Club.Projects.ManagerRoot, 8), 0, finance);
        var manager = memory.Map(finance, 0x40);
        TestWorld.Set64(manager, 0, module + profile.Club.Projects.ManagerVtable);
        TestWorld.Set64(manager, 0x18, expansions);
        foreach (int field in new[] { 0x20, 0x28 })
        {
            TestWorld.Set64(manager, field, 0x640D_0000 + (ulong)field);
            memory.Map(0x640D_0000 + (ulong)field, 24);
        }
        var queue = memory.Map(expansions, 24);
        TestWorld.Set64(queue, 0, slots);
        TestWorld.Set64(queue, 8, slots + 8);
        TestWorld.Set64(queue, 16, slots + 8);
        TestWorld.Set64(memory.Map(slots, 8), 0, works);
        var construction = memory.Map(works, 0x40);
        TestWorld.Set64(construction, 0, clubAddress);
        TestWorld.Set64(construction, 8, newGround);
        BitConverter.GetBytes(0x07EE0001u).CopyTo(construction, 0x18);
        BitConverter.GetBytes(0x076C0001u).CopyTo(construction, 0x1C);
        BitConverter.GetBytes(20000).CopyTo(construction, 0x30);
        BitConverter.GetBytes(20000).CopyTo(construction, 0x34);
        foreach (ulong ground in new[] { oldGround, newGround })
        {
            var bytes = memory.Map(ground, 0x100);
            TestWorld.Set64(bytes, 0, module + profile.Stadium!.VtableRva);
        }
        TestWorld.Set64(memory.Map(newGround, 0x100), profile.Stadium!.Owner, clubAddress);

        TestWorld.Set64(memory.Map(module + layout.TableRoot, 8), 0, table);
        var tableBytes = memory.Map(table, layout.Vector + 8);
        TestWorld.Set64(tableBytes, 0, module + layout.TableVtable);
        TestWorld.Set64(tableBytes, layout.Vector, header);
        var changes = memory.Map(header, 24);
        TestWorld.Set64(changes, 0, changeSlots);
        TestWorld.Set64(changes, 8, changeSlots + 16);
        TestWorld.Set64(changes, 16, changeSlots + 16);
        var changeTable = memory.Map(changeSlots, 16);
        TestWorld.Set64(changeTable, 0, move);
        TestWorld.Set64(changeTable, 8, capacity);
        var moveRow = MapChange(move, "C0B6E772010000002F000000A9000000A90000000000000028B0B5F67A000000C08BA8F67A000000002497F67A0000000100EE070100EE070100EE07FFFFFFFFFFFFFFFFFFFFFFFF0000000000000000000000000000000000000000FFFFFFFFFFFFFFFF0100FFFFFFFF000000000000");
        TestWorld.Set64(moveRow, 0x18, team);
        TestWorld.Set64(moveRow, 0x20, oldGround);
        TestWorld.Set64(moveRow, 0x28, newGround);
        var capacityRow = MapChange(capacity, "C0B6E7720100000056000000FB010000FB010000000000000000000000000000002497F67A00000000000000000000000100EE070100EE0701006C07204E0000204E0000000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF0200FFFFFF01000000000000");
        TestWorld.Set64(capacityRow, 0x20, newGround);

        TestWorld.Set64(memory.Map(module + profile.GameDate!.Primary.RootRva, 8), 0, 0x640F_0000);
        var date = memory.Map(0x640F_0000, profile.GameDate.Primary.DateOffset + 4);
        TestWorld.Set64(date, 0, module + profile.GameDate.Primary.ObjectVtableRva!.Value);
        BitConverter.GetBytes(0x07EC003Eu).CopyTo(date, profile.GameDate.Primary.DateOffset); // 2 March 2028
        var today = new DateOnly(2028, 3, 2);

        var editor = new ClubProjects(memory, profile, module);
        var project = Assert.Single(editor.Read(club));
        Assert.Equal((works, capacity, move), (project.Address, project.ChangeDateAddress - 0x34, project.MoveRow));
        editor.BringForward(club, project, today);
        Assert.Equal(0x07EC003Eu, BitConverter.ToUInt32(construction, 0x18));
        Assert.Equal("0100EE073E00EC073E00EC07", Convert.ToHexString(moveRow, 0x30, 12));
        Assert.Equal("0100EE073E00EC0701006C07", Convert.ToHexString(capacityRow, 0x30, 12));
        var roundTrip = OffsetProfile.Load(profile.ToJson());
        Assert.Equal(layout.Move, roundTrip.Club!.Projects!.StadiumChanges!.Move);

        byte[] MapChange(ulong address, string hex)
        {
            var row = memory.Map(address, 0x70);
            Convert.FromHexString(hex).CopyTo(row, 0);
            TestWorld.Set64(row, 0, module + layout.Vtable);
            return row;
        }
    }

    static void MapEmptyStadiumChanges(FakeMemory memory, OffsetProfile profile)
    {
        if (profile.Club?.Projects?.StadiumChanges is not { } layout) return;
        const ulong table = 0x6240_0000, header = 0x6241_0000;
        TestWorld.Set64(memory.Map(TestWorld.ModuleBase + layout.TableRoot, 8), 0, table);
        var tableBytes = memory.Map(table, layout.Vector + 8);
        TestWorld.Set64(tableBytes, 0, TestWorld.ModuleBase + layout.TableVtable);
        TestWorld.Set64(tableBytes, layout.Vector, header);
        memory.Map(header, 24);
    }
}
