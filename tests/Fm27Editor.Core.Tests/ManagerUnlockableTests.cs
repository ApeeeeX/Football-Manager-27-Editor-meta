using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

public class ManagerUnlockableTests
{
    [Theory]
    [InlineData("27.1.0+2505117-mac.json", 0x5678900u)]
    [InlineData("27.1.0+2505719-touch-mac.json", 0x581E278u)]
    public void Sets_either_unlockable_on_the_human_manager_and_preserves_every_other_bit(string build, uint vtable)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var world = new TestWorld();
        const ulong address = 0x6000_0000;
        var manager = world.Memory.Map(address, 0x640);
        TestWorld.Set64(manager, 0, TestWorld.ModuleBase + vtable);
        BitConverter.GetBytes(222000001).CopyTo(manager, 0x554);
        // FM27's ARM64 engine tests bit 0 for interest and writes mask 2 for Become Unsackable.
        BitConverter.GetBytes(0x4100400Au).CopyTo(manager, 0x3FC);
        var repo = new StaffRepository(world.Memory, profile, TestWorld.ModuleBase);
        var writer = new StaffWriter(world.Memory, profile, TestWorld.ModuleBase);
        var before = repo.Reload(address)!;
        Assert.Equal(new ManagerUnlockableState(0x4100400A, false, true), before.ManagerUnlockables);

        writer.SetManagerUnlockables(before, allPlayersInterested: true, becomeUnsackable: null);
        Assert.Equal(0x4100400Bu, BitConverter.ToUInt32(manager, 0x3FC));
        Assert.Throws<StaleObjectException>(() => writer.SetManagerUnlockables(before, null, false));

        var current = repo.Reload(address)!;
        writer.SetManagerUnlockables(current, allPlayersInterested: null, becomeUnsackable: false);
        Assert.Equal(new ManagerUnlockableState(0x41004009, true, false), repo.Reload(address)!.ManagerUnlockables);

        var latest = repo.Reload(address)!;
        BitConverter.GetBytes(222000002).CopyTo(manager, 0x554);
        Assert.Throws<StaleObjectException>(() => writer.SetManagerUnlockables(latest, false, true));
        Assert.Equal(0x41004009u, BitConverter.ToUInt32(manager, 0x3FC));
    }
}
