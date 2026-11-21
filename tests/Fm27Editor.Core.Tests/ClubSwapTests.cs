using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.Tests;

public class ClubSwapTests
{
    [Fact]
    public void Swaps_both_tables_and_fixture_copies_and_preserves_unrelated_penalties_and_dates()
    {
        var world = new LeagueWorld();
        var preview = world.Preview();
        Assert.Equal(6, preview.Fixtures.Count);
        Assert.Equal(8, preview.Fixtures.Sum(match => match.RoundRobinCopies));
        Assert.Equal(new DateOnly(2026, 8, 21), preview.Fixtures.First(row => row.Competition.Id == 11).Date);

        var journal = new ChangeJournal();
        world.Memory.Journal = journal;
        Func<ulong, int, ChangeLabel?>? labels = null;
        using (journal.Begin("Club", "A / C", "Swap Clubs", (address, length) => labels?.Invoke(address, length)))
            labels = new ClubSwap(world.Memory, world.Profile, LeagueWorld.Module).Apply(preview);

        Assert.Equal(world.SecondLeague, world.Q(world.Teams[0] + 0x50));
        Assert.Equal(world.FirstLeague, world.Q(world.Teams[2] + 0x50));
        foreach (ulong row in world.FirstRows) Assert.Equal(world.Teams[2], world.Q(row + 0x88));
        foreach (ulong row in world.SecondRows) Assert.Equal(world.Teams[0], world.Q(row + 0x88));
        Assert.Equal(-4, BitConverter.ToInt16(world.Memory.Read(world.PenalizedRow + 0xC, 2)));
        foreach (ulong fixture in world.FirstFixtureCopies)
        {
            Assert.Equal(world.Teams[2], world.Q(fixture + 8));
            Assert.Equal(world.Teams[1], world.Q(fixture + 16));
            Assert.Equal(world.Grounds[2], world.Q(fixture + 0x28));
            Assert.Equal(0x07EA72E9u, BitConverter.ToUInt32(world.Memory.Read(fixture + 0x4C, 4)));
        }
        Assert.Equal(world.Teams[0], world.Q(world.SecondAwayFixture + 16));
        Assert.Equal(world.Grounds[3], world.Q(world.SecondAwayFixture + 0x28));
        Assert.Equal(world.Teams[2], world.Q(world.FirstStats + 0x18));
        Assert.Equal(world.Teams[0], world.Q(world.SecondStats + 0x18));

        var group = Assert.Single(journal.Groups);
        Assert.False(journal.Undo(group.Entries[0], world.Memory, out _));
        Assert.Equal(group.Entries.Count, journal.Undo(group, world.Memory).Restored);
        Assert.Equal(world.FirstLeague, world.Q(world.Teams[0] + 0x50));
        Assert.Equal(world.SecondLeague, world.Q(world.Teams[2] + 0x50));
        Assert.Equal(world.Teams[0], world.Q(world.FirstFixtureCopies[1] + 8));
    }

    [Fact]
    public void Refuses_an_unexpected_venue_before_any_write()
    {
        var world = new LeagueWorld();
        var preview = world.Preview();
        world.Memory.TryWrite(world.FirstFixtureCopies[1] + 0x28, BitConverter.GetBytes(world.Grounds[1]));
        int writes = world.Memory.Inner.Writes;
        Assert.Throws<StaleObjectException>(() => new ClubSwap(world.Memory, world.Profile, LeagueWorld.Module).Apply(preview));
        Assert.Equal(writes, world.Memory.Inner.Writes);
    }

    sealed class LeagueWorld
    {
        public const ulong Module = 0x1000_0000_0000;
        public OffsetProfile Profile { get; } = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505719-touch-mac.json"));
        public SimulatedGame Memory { get; } = new();
        public ulong FirstLeague { get; }
        public ulong SecondLeague { get; }
        public ulong[] Teams { get; } = new ulong[5];
        public ulong[] Grounds { get; } = new ulong[5];
        public ulong[] FirstRows { get; }
        public ulong[] SecondRows { get; }
        public ulong PenalizedRow { get; }
        public ulong[] FirstFixtureCopies { get; }
        public ulong SecondAwayFixture { get; }
        public ulong FirstStats { get; }
        public ulong SecondStats { get; }
        readonly ClubEntry[] clubs = new ClubEntry[5];
        ulong next = 0x2000_0000;

        public LeagueWorld()
        {
            var c = Profile.Competition!;
            var k = Profile.Club!;
            ulong nation = Allocate(8, out _);
            FirstLeague = League(11, out ulong firstStage, out ulong firstOwner);
            SecondLeague = League(12, out ulong secondStage, out ulong secondOwner);
            for (int i = 0; i < Teams.Length; i++)
            {
                ulong club = Allocate(0x200, out var clubBytes);
                Qword(clubBytes, 0, Module + k.ClubVtableRva);
                Qword(clubBytes, k.Nation!.Value, nation);
                BitConverter.GetBytes(100 + i).CopyTo(clubBytes, k.Id!.Value);
                Teams[i] = Allocate(0xB8, out var team);
                Grounds[i] = Allocate(8, out var ground);
                Qword(ground, 0, Module + Profile.Stadium!.VtableRva);
                Qword(team, 0, Module + Profile.Team!.VtableRva);
                Qword(team, 0x30, club);
                Qword(team, 0x78, Grounds[i]);
                Qword(team, 0x50, i < 2 ? FirstLeague : SecondLeague);
                clubs[i] = new(club, 100 + i, $"Club {(char)('A' + i)}", null, 0, [new(Teams[i], club, 0, 0, 0, true)]);
            }
            FirstStats = EnterLeague(0, FirstLeague);
            SecondStats = EnterLeague(2, SecondLeague);
            var primaryFirst = Table(firstStage + 0xC0, [0, 1], true);
            var mirrorFirst = Table(firstStage + 0x140, [1, 0], false);
            var primarySecond = Table(secondStage + 0xC0, [2, 3, 4], true);
            var mirrorSecond = Table(secondStage + 0x140, [4, 3, 2], false);
            FirstRows = [primaryFirst[0], mirrorFirst[1]];
            SecondRows = [primarySecond[0], mirrorSecond[2]];
            PenalizedRow = primarySecond[2];

            FirstFixtureCopies = [Fixture(firstOwner, 0, 1, 0x07EA72E9), Fixture(firstOwner, 0, 1, 0x07EA72E9)];
            Fixture(firstOwner, 1, 0, 0x07EA7307);
            Fixture(secondOwner, 2, 3, 0x07EA72E3);
            SecondAwayFixture = Fixture(secondOwner, 3, 2, 0x07EA72F2);
            Fixture(secondOwner, 2, 4, 0x07EA72EA);
            Fixture(secondOwner, 4, 2, 0x07EA7300);
            Fixture(secondOwner, 4, 2, 0x07EA7300);
            Fixture(secondOwner, 3, 4, 0x07EA72EA);
            Fixture(secondOwner, 4, 3, 0x07EA72F2);

            SetupClock(Profile.GameDate!.Primary);
            if (Profile.GameDate.CrossCheck is { } crossCheck) SetupClock(crossCheck);

            ulong League(int id, out ulong stage, out ulong owner)
            {
                ulong league = Allocate(0x1B0, out var leagueBytes);
                Qword(leagueBytes, 0, Module + c.VtableRva);
                BitConverter.GetBytes(id).CopyTo(leagueBytes, c.Id!.Value);
                var prize = c.PrizeMoney!;
                ulong holder = Allocate(0x300, out var holderBytes);
                Qword(holderBytes, 0, Module + prize.HolderVtableRva);
                Qword(leagueBytes, prize.Holder, holder);
                stage = Allocate(0x190, out var stageBytes);
                Qword(stageBytes, 0, Module + prize.StageVtableRva);
                Qword(stageBytes, prize.StageOwner, league);
                Vector(holder + (ulong)prize.Stages, 8, out var stages);
                Qword(stages, 0, stage);
                ulong prizeGroup = Allocate(0x200, out var prizeBytes);
                Qword(prizeBytes, 0, Module + prize.PrizeVtableRva);
                Qword(stageBytes, prize.StagePrize, prizeGroup);
                owner = Allocate(0x40, out var ownerBytes);
                Qword(ownerBytes, 0, Module + 0x581D768);
                Qword(ownerBytes, 0x18, league);
                return league;
            }

            ulong EnterLeague(int index, ulong league)
            {
                ulong state = Allocate(0x70, out _);
                QwordAt(Teams[index] + 0xA0, state);
                Vector(state + 0x50, 0x18, out var entries);
                ulong stats = Allocate(0x38, out var contents);
                Qword(contents, 0, Module + 0x5810DD8);
                Qword(contents, 0x18, Teams[index]);
                Qword(entries, 0, league);
                Qword(entries, 8, stats);
                return stats;
            }

            ulong[] Table(ulong header, int[] members, bool primary)
            {
                Vector(header, members.Length * 8, out var pointers);
                return members.Select((index, row) =>
                {
                    ulong at = Allocate(0x100, out var data);
                    Qword(pointers, row * 8, at);
                    Qword(data, 0x88, Teams[index]);
                    if (primary) Qword(data, 0x78, clubs[index].Address + 0xE0);
                    if (index == 4) BitConverter.GetBytes((short)-4).CopyTo(data, 0xC);
                    return at;
                }).ToArray();
            }

            ulong Fixture(ulong owner, int home, int away, uint date)
            {
                ulong address = Allocate(0x58, out var data);
                Memory.Regions.Add(new(address, 0x58, false));
                Qword(data, 0, Module + 0x58E49F0);
                Qword(data, 8, Teams[home]);
                Qword(data, 0x10, Teams[away]);
                Qword(data, 0x20, owner);
                Qword(data, 0x28, Grounds[home]);
                BitConverter.GetBytes((ushort)2026).CopyTo(data, 0x30);
                BitConverter.GetBytes(date).CopyTo(data, 0x4C);
                return address;
            }

            void SetupClock(DateChain clock)
            {
                ulong obj = Allocate(clock.PointerOffset is { } offset ? offset + 8 : clock.DateOffset + 4, out var objBytes);
                Qword(Memory.Inner.Map(Module + clock.RootRva, 8), 0, obj);
                if (clock.ObjectVtableRva is { } vt) Qword(objBytes, 0, Module + vt);
                var node = objBytes;
                if (clock.PointerOffset is { } pointer)
                {
                    ulong target = Allocate(clock.DateOffset + 4, out node);
                    Qword(objBytes, pointer, target);
                }
                BitConverter.GetBytes(0x07EA00DCu).CopyTo(node, clock.DateOffset); // 8 August 2026.
            }
        }

        ulong Allocate(int length, out byte[] bytes)
        {
            ulong address = next;
            next += 0x1000;
            bytes = Memory.Inner.Map(address, length);
            return address;
        }

        void Vector(ulong header, int length, out byte[] data)
        {
            ulong begin = Allocate(length, out data);
            QwordAt(header, begin);
            QwordAt(header + 8, begin + (ulong)length);
            QwordAt(header + 16, begin + (ulong)length);
        }

        public ClubSwapSchedule Preview() => new ClubSwapPreview(Memory, Profile, Module).Read(clubs[0], clubs[2]);
        public ulong Q(ulong address) => Memory.ReadUInt64(address);
        void QwordAt(ulong address, ulong value) => Assert.True(Memory.Inner.TryWrite(address, BitConverter.GetBytes(value)));
        static void Qword(byte[] bytes, int offset, ulong value) => TestWorld.Set64(bytes, offset, value);
    }

    sealed class SimulatedGame : IProcessMemory, IMemoryRegions, ICodePatchHost
    {
        public FakeMemory Inner { get; } = new();
        public List<MemoryRegion> Regions { get; } = [];
        public ChangeJournal? Journal { get; set; }
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => Regions;
        public bool TryRead(ulong address, Span<byte> destination) => Inner.TryRead(address, destination);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            byte[] before = Inner.Read(address, data.Length);
            if (!Inner.TryWrite(address, data)) return false;
            Journal?.Record(address, before, data.ToArray());
            return true;
        }
        public IDisposable Pause() => new PauseScope();
        public ulong AllocateNear(ulong near, int bytes) => throw new NotSupportedException();
        public uint Protect(ulong address, int bytes, uint protect) => throw new NotSupportedException();
        public void FlushCode(ulong address, int bytes) { }
        sealed class PauseScope : IDisposable { public void Dispose() { } }
    }
}
