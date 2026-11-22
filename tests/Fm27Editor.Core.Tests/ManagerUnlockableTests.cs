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

    /// <summary>
    /// Windows 27.1.0: turning Become Unsackable on in the IGE (Steam, calibration save, manager Duong Vu ID 2002081897,
    /// 22/11/2026) changed the word at staff + 0x3FC from 0x00000000 to 0x00000002 and no other non-pointer field.
    /// </summary>
    [Theory]
    [InlineData("27.1.0+2505117-steam.json", 0x4E2170Cu)]
    [InlineData("27.1.0+2505117-gamepass.json", 0x4A471CCu)]
    public void Become_unsackable_is_bit_two_of_the_word_the_ige_sets_on_windows(string build, uint vtable)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build));
        var world = new TestWorld();
        const ulong address = 0x6000_0000;
        var manager = world.Memory.Map(address, 0x640);
        TestWorld.Set64(manager, 0, TestWorld.ModuleBase + vtable);
        BitConverter.GetBytes(2002081897).CopyTo(manager, 0x440 + 0xC);
        var repo = new StaffRepository(world.Memory, profile, TestWorld.ModuleBase);
        var writer = new StaffWriter(world.Memory, profile, TestWorld.ModuleBase);
        var before = repo.Reload(address)!;
        Assert.Equal(new ManagerUnlockableState(0, false, false), before.ManagerUnlockables);

        writer.SetManagerUnlockables(before, allPlayersInterested: null, becomeUnsackable: true);
        Assert.Equal(2u, BitConverter.ToUInt32(manager, 0x3FC));
        Assert.Equal(new ManagerUnlockableState(2, false, true), repo.Reload(address)!.ManagerUnlockables);
    }
}
