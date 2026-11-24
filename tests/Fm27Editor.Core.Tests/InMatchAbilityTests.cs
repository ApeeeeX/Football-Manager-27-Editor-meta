using System.Buffers.Binary;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class InMatchAbilityTests
{
    const int Dribbling = 0x488, Crossing = 0x487, InjuryProneness = 0x4B7;
    const int ConditionInitial = 0x120A, Condition = 0x1220, Morale = 0x1233, Striker = 0x484;
    static readonly ClubInfo United = new(0x100, "Man Utd", "Man Utd");
    static readonly ClubInfo Arsenal = new(0x200, "Arsenal", "Arsenal");
    static readonly ClubInfo Barcelona = new(0x300, "Barcelona", "Barcelona");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovers_both_copies_of_a_fixture_and_assigns_a_loan_player_to_the_match_side(bool touch)
    {
        var (editor, players, _, _) = BuildFixture(touch);
        var match = Assert.Single(editor.FindFixtures(players));
        Assert.Equal(new[] { United.Address, Arsenal.Address }, match.Teams.Select(x => x.Address));
        Assert.Equal(15, match.Members.Count);
        Assert.Equal(8, match.Members.Count(x => x.Side == United));
        var loanee = Assert.Single(match.Members, x => x.Player.Id == 8);
        Assert.Equal(Barcelona, loanee.Player.Club);
        Assert.Equal(United, loanee.Side);
        Assert.True(match.IsSameFixture(Assert.Single(editor.FindFixtures(players))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Applies_attribute_overrides_and_both_condition_words_to_the_selected_match_copies_only(bool touch)
    {
        var (editor, players, copies, permanent) = BuildFixture(touch);
        var match = Assert.Single(editor.FindFixtures(players));
        var changes = new InMatchChanges(true, new Dictionary<PlayerAttribute, int> { [PlayerAttribute.Dribbling] = 19 }, 100, 20);
        var untouchedOpponent = copies[1, 9].ToArray();
        var untouchedTeammate = copies[1, 2].ToArray();
        var originalPlayer = permanent[1].ToArray();
        var targets = new HashSet<int> { 1, 2, 8 };
        var result = editor.Apply(match, targets, changes, PlayerPosition.Striker, 18);
        Assert.Equal(new InMatchResult(2, 4), result);
        foreach (var i in new[] { 0, 1 })
            foreach (var uid in new[] { 1, 8 })
            {
                Assert.Equal(100, copies[i, uid][Crossing]);
                Assert.Equal(95, copies[i, uid][Dribbling]);
                Assert.Equal(15, copies[i, uid][InjuryProneness]);
                Assert.Equal(10000, ReadCondition(copies[i, uid], ConditionInitial));
                Assert.Equal(10000, ReadCondition(copies[i, uid], Condition));
                Assert.Equal(20, copies[i, uid][Morale]);
            }
        Assert.Equal(untouchedOpponent, copies[1, 9]);
        Assert.Equal(untouchedTeammate, copies[1, 2]);
        Assert.Equal(originalPlayer, permanent[1]);
        Assert.Equal(default, editor.Apply(match, targets, changes, PlayerPosition.Striker, 18));
    }

    [Fact]
    public void Refuses_an_out_of_range_condition_and_aborts_all_writes_if_a_match_copy_was_reused()
    {
        var (editor, players, copies, _) = BuildFixture();
        var match = Assert.Single(editor.FindFixtures(players));
        var before = copies[0, 1].ToArray();
        var targets = new HashSet<int> { 1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Apply(match, targets, new(false, new Dictionary<PlayerAttribute, int>(), 49)));
        TestWorld.Set64(copies[1, 1], 0x280, 0x3_0000);
        Assert.Throws<StaleObjectException>(() => editor.Apply(match, targets, new(false, new Dictionary<PlayerAttribute, int>(), 100)));
        Assert.Equal(before, copies[0, 1]);
        Assert.Equal(7500, ReadCondition(copies[1, 1], Condition));
    }

    [Theory]
    [InlineData(false, 1, 5)]
    [InlineData(false, 12, 60)]
    [InlineData(true, 16, 80)]
    [InlineData(true, 20, 100)]
    public void Reads_live_values_and_applies_exact_ratings_to_selected_players_from_both_teams(bool touch, int rating, byte expectedRaw)
    {
        var (editor, players, copies, permanent) = BuildFixture(touch);
        var match = Assert.Single(editor.FindFixtures(players));
        var initial = editor.ReadPlayers(match);
        Assert.Equal(17, initial[1].Attributes[PlayerAttribute.Dribbling]);
        Assert.Equal(75, initial[1].ConditionPercent);
        Assert.Equal(12, initial[1].Morale);

        var ratings = editor.Attributes.ToDictionary(attribute => attribute, _ => rating);
        var unchanged = copies[0, 10].ToArray();
        var permanentPlayer = permanent[1].ToArray();
        var selected = new HashSet<int> { 1, 9 };
        var applied = editor.Apply(match, selected, new(false, ratings, 100, 20));

        Assert.Equal(new InMatchResult(2, 4), applied);
        Assert.Equal(expectedRaw, copies[0, 1][Dribbling]);
        Assert.Equal(expectedRaw, copies[1, 9][Crossing]);
        Assert.Equal(expectedRaw, copies[1, 9][InjuryProneness]);
        Assert.Equal(10000, ReadCondition(copies[0, 9], Condition));
        Assert.Equal(20, copies[0, 9][Morale]);
        Assert.Equal(unchanged, copies[0, 10]);
        Assert.Equal(permanentPlayer, permanent[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Match_offsets_round_trip_through_the_profile(bool touch)
    {
        var profile = LoadProfile(touch);
        Assert.NotNull(profile.InMatch);
        Assert.Equal(profile.InMatch, OffsetProfile.Load(profile.ToJson()).InMatch);
    }

    [Fact]
    public void Windows_steam_profile_reads_live_condition_from_active_replica_and_updates_active_slots()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        var im = Assert.IsType<InMatchOffsets>(profile.InMatch);
        Assert.NotNull(im.LiveCondition);
        Assert.Equal(im, OffsetProfile.Load(profile.ToJson()).InMatch);

        var (editor, players, copies) = BuildSteamFixture(profile, replicaCount: 4);
        var match = Assert.Single(editor.FindFixtures(players));
        var values = editor.ReadPlayers(match);
        Assert.Equal(75, values[1].ConditionPercent);
        Assert.Equal(20, values[1].Morale);

        var (kickoffEditor, kickoffPlayers, _) = BuildSteamFixture(profile, replicaCount: 2);
        var kickoffMatch = Assert.Single(kickoffEditor.FindFixtures(kickoffPlayers));
        var kickoffValues = kickoffEditor.ReadPlayers(kickoffMatch);
        Assert.Equal(81, kickoffValues[1].ConditionPercent);
        Assert.Equal(17, kickoffValues[1].Morale);

        var applied = editor.Apply(match, new HashSet<int> { 1 }, new(false, new Dictionary<PlayerAttribute, int>(), 100, 18));
        Assert.Equal(new InMatchResult(1, 4), applied);
        Assert.Equal(0, ReadCondition(copies[0, 1], im.Condition));
        Assert.Equal(10000, ReadCondition(copies[0, 1], im.InitialCondition));
        Assert.Equal(18, copies[0, 1][im.Morale!.Value]);
        foreach (int replica in new[] { 1, 2, 3 })
        {
            Assert.Equal(10000, ReadCondition(copies[replica, 1], im.InitialCondition));
            Assert.Equal(10000, ReadCondition(copies[replica, 1], im.Condition));
            Assert.Equal(1_000_000u, BinaryPrimitives.ReadUInt32LittleEndian(copies[replica, 1].AsSpan(im.LiveCondition!.Value)));
            Assert.Equal(18, copies[replica, 1][im.Morale!.Value]);
        }
    }

    static (InMatchAbility Editor, List<PlayerRecord> Players, byte[,][] Copies) BuildSteamFixture(OffsetProfile profile, int replicaCount)
    {
        var im = profile.InMatch!;
        uint playerVt = profile.PlayerVtableRvas[0];
        int personOffset = profile.Relations!.PlayerPerson;
        var memory = new MatchMemory();
        var players = new List<PlayerRecord>();
        var copies = new byte[replicaCount, 16][];
        (ushort CondU16, uint LiveU32, byte Morale)[] states =
        [
            (0, 1_000_000u, 17),
            (8000, 807_900u, 17),
            (7400, 745_422u, 20),
            (7400, 742_167u, 19),
        ];
        for (int uid = 1; uid <= 15; uid++)
        {
            var club = uid < 8 ? United : Arsenal;
            ulong address = 0x1000_0000 + (ulong)uid * 0x1000;
            byte[] bytes = memory.Map(address, profile.PlayerObjectSize);
            TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + playerVt);
            TestWorld.Set64(bytes, personOffset, TestWorld.ModuleBase + im.PersonVtable);
            BitConverter.GetBytes(uid).CopyTo(bytes, personOffset + im.PersonUid);
            players.Add(new PlayerRecord
            {
                Address = address, VtableRva = playerVt, Id = uid, FirstName = $"Player {uid}",
                CurrentAbility = 140, PotentialAbility = 160, HomeReputation = 0, CurrentReputation = 0, WorldReputation = 0, Height = 180,
                Contract = new ContractInfo(0, 0, null, null, club), AttributeScale = profile.AttributeScale, RawAttributes = new byte[54],
                Positions = new byte[14], Personality = new byte[8],
            });
            for (int replica = 0; replica < replicaCount; replica++)
            {
                ulong matchAddress = 0x2000_0000 + (ulong)replica * 0x10_0000 + (ulong)uid * 0x2400;
                var match = memory.Map(matchAddress, 0x2400, find: true);
                copies[replica, uid] = match;
                TestWorld.Set64(match, 0, TestWorld.ModuleBase + im.PlayerVtable);
                TestWorld.Set64(match, im.Data, TestWorld.ModuleBase + im.DataVtable);
                TestWorld.Set64(match, im.SourcePerson, address + (ulong)personOffset);
                TestWorld.Set64(match, im.Fixture, 0x1_0000 + (ulong)replica * 0x1000);
                TestWorld.Set64(match, im.Side, replica == 0 ? 0x2_0000UL : uid < 8 ? 0x2_1000UL : 0x2_2000UL);
                TestWorld.Set16(match, im.InitialCondition, 8079);
                BinaryPrimitives.WriteUInt16LittleEndian(match.AsSpan(im.Condition), states[replica].CondU16);
                BinaryPrimitives.WriteUInt32LittleEndian(match.AsSpan(im.LiveCondition!.Value), states[replica].LiveU32);
                match[im.Morale!.Value] = states[replica].Morale;
            }
        }
        return (new InMatchAbility(memory, profile, TestWorld.ModuleBase), players, copies);
    }

    static int ReadCondition(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));

    static OffsetProfile LoadProfile(bool touch) => OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets",
        touch ? "27.1.0+2505719-touch-mac.json" : "27.1.0+2505117-mac.json"));

    static (InMatchAbility Editor, List<PlayerRecord> Players, byte[,][] Copies, Dictionary<int, byte[]> Permanent) BuildFixture(bool touch = false)
    {
        var profile = LoadProfile(touch);
        // Independent vtable/RTTI measurements from each game's ARM64 image, and the permanent player rating offset.
        (uint playerVtable, uint personVtable, int person, int dribbling, uint matchVtable, uint dataVtable) = touch
            ? (0x57FACE0u, 0x57FC578u, 0x2D8, 0xA8, 0x5910CB0u, 0x59104A8u)
            : (0x5655368u, 0x5656C00u, 0x388, 0x158, 0x576BBF0u, 0x576B3E8u);
        var memory = new MatchMemory();
        var players = new List<PlayerRecord>();
        var copies = new byte[2, 16][];
        var permanent = new Dictionary<int, byte[]>();
        for (int uid = 1; uid <= 15; uid++)
        {
            var club = uid < 8 ? United : uid == 8 ? Barcelona : Arsenal;
            ulong address = 0x1000_0000 + (ulong)uid * 0x1000;
            byte[] bytes = memory.Map(address, profile.PlayerObjectSize);
            TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + playerVtable);
            TestWorld.Set64(bytes, person, TestWorld.ModuleBase + personVtable);
            BitConverter.GetBytes(uid).CopyTo(bytes, person + 0xC);
            bytes[dribbling] = 85;
            permanent.Add(uid, bytes);
            players.Add(new PlayerRecord
            {
                Address = address, VtableRva = playerVtable, Id = uid, FirstName = $"Player {uid}",
                CurrentAbility = 140, PotentialAbility = 160, HomeReputation = 0, CurrentReputation = 0, WorldReputation = 0, Height = 180,
                Contract = new ContractInfo(0, 0, null, null, club), AttributeScale = 5, RawAttributes = new byte[54],
                Positions = new byte[14], Personality = new byte[8],
            });
            for (int copy = 0; copy < 2; copy++)
            {
                ulong matchAddress = 0x2000_0000 + (ulong)copy * 0x10_0000 + (ulong)uid * 0x2000;
                var match = memory.Map(matchAddress, 0x1240, find: true);
                copies[copy, uid] = match;
                TestWorld.Set64(match, 0, TestWorld.ModuleBase + matchVtable);
                TestWorld.Set64(match, 0x470, TestWorld.ModuleBase + dataVtable);
                TestWorld.Set64(match, 0x20, address + (ulong)person);
                TestWorld.Set64(match, 0x280, 0x1_0000 + (ulong)copy * 0x1000);
                TestWorld.Set64(match, 0x288, copy == 0 ? 0x2_0000UL : uid <= 8 ? 0x2_1000UL : 0x2_2000UL);
                match[Crossing] = 45;
                match[Dribbling] = 85;
                match[InjuryProneness] = 15;
                match[Morale] = 12;
                match[Striker] = uid is 1 or 8 ? (byte)20 : (byte)15;
                TestWorld.Set16(match, ConditionInitial, 7500);
                TestWorld.Set16(match, Condition, 7500);
            }
        }
        return (new InMatchAbility(memory, profile, TestWorld.ModuleBase), players, copies, permanent);
    }

    sealed class MatchMemory : IProcessMemory, IMemoryRegions
    {
        readonly FakeMemory memory = new();
        readonly List<MemoryRegion> regions = [];
        public byte[] Map(ulong address, int size, bool find = false)
        {
            if (find) regions.Add(new(address, (ulong)size, false));
            return memory.Map(address, size);
        }
        public bool TryRead(ulong address, Span<byte> value) => memory.TryRead(address, value);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> value) => memory.TryWrite(address, value);
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) => regions;
    }
}
