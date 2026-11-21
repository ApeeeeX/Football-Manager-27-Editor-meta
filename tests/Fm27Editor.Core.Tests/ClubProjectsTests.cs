using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public class ClubProjectsTests
{
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
}
