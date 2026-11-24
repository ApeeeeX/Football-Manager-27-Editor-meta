using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;
using Xunit;

namespace Fm27Editor.Core.Tests;

public class TeamTacticalUnderstandingTests
{
    /// <summary>Man Utd tactic on Steam 27.1.0 macOS, 23/11/2026: after the game's Maximise, all 28 players held these options at 100%.</summary>
    [Fact]
    public void Options_match_what_the_game_maximised()
        => Assert.Equal([14, 20, 1, 17, 20, 4], TeamTacticalUnderstanding.OptionsOf(0x28A05008400330A4, 0x00000000000A8162));

    /// <summary>Steam 27.1.0 Windows, 23/11/2026: live human-managed team 540 tactic flags decode to the same option mapping.</summary>
    [Fact]
    public void Options_decode_live_windows_tactic_flags()
        => Assert.Equal([19, 17, 10, 17, 20, 12], TeamTacticalUnderstanding.OptionsOf(0x2250500A008230A4, 0x0000000000000064));

    [Fact]
    public void Reads_tactic_options_from_msvc_std_map_tree_with_sentinel()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        var memory = new FakeMemory();
        const ulong module = 0x1_0000_0000, team = 0x2_0000_0000, missingTeam = 0x2_0000_0400;
        const ulong managers = 0x3_0000_0000, entries = 0x3_0001_0000, entry = 0x3_0002_0000;
        const ulong head = 0x3_0003_0000, rootNode = 0x3_0004_0000, rightNode = 0x3_0005_0000;
        const ulong vec = 0x3_0006_0000, tactic = 0x3_0007_0000;

        BitConverter.GetBytes(100).CopyTo(memory.Map(team, 0x20), 8);
        BitConverter.GetBytes(999).CopyTo(memory.Map(missingTeam, 0x20), 8);

        BitConverter.GetBytes(managers).CopyTo(memory.Map(module + profile.TeamTactics!.ManagersRva, 8), 0);
        var mgr = memory.Map(managers, 0x40);
        BitConverter.GetBytes(entries).CopyTo(mgr, profile.TeamTactics.Entries);
        BitConverter.GetBytes(entries + 8).CopyTo(mgr, profile.TeamTactics.Entries + 8);
        BitConverter.GetBytes(entry).CopyTo(memory.Map(entries, 8), 0);
        BitConverter.GetBytes(head).CopyTo(memory.Map(entry, 0x10), profile.TeamTactics.TeamTree);

        // MSVC _Myhead sentinel has _Isnil = 1 at +0x19 and _Parent at +0x8 pointing to rootNode (key 50 < 100 -> right child at +0x10).
        var headBytes = memory.Map(head, 0x30);
        BitConverter.GetBytes(head).CopyTo(headBytes, 0);
        BitConverter.GetBytes(rootNode).CopyTo(headBytes, 8);
        BitConverter.GetBytes(head).CopyTo(headBytes, 0x10);
        headBytes[0x19] = 1;

        var rootBytes = memory.Map(rootNode, 0x30);
        BitConverter.GetBytes(head).CopyTo(rootBytes, 0);
        BitConverter.GetBytes(head).CopyTo(rootBytes, 8);
        BitConverter.GetBytes(rightNode).CopyTo(rootBytes, 0x10);
        rootBytes[0x19] = 0;
        BitConverter.GetBytes(50).CopyTo(rootBytes, 0x20);

        var rightBytes = memory.Map(rightNode, 0x1100);
        BitConverter.GetBytes(head).CopyTo(rightBytes, 0);
        BitConverter.GetBytes(rootNode).CopyTo(rightBytes, 8);
        BitConverter.GetBytes(head).CopyTo(rightBytes, 0x10);
        rightBytes[0x19] = 0;
        BitConverter.GetBytes(100).CopyTo(rightBytes, 0x20);

        BitConverter.GetBytes(vec).CopyTo(rightBytes, 0x28 + profile.TeamTactics.Tactics);
        BitConverter.GetBytes(vec + 8).CopyTo(rightBytes, 0x28 + profile.TeamTactics.Tactics + 8);
        BitConverter.GetBytes(tactic).CopyTo(memory.Map(vec, 8), 0);
        var tacBytes = memory.Map(tactic, 0x100);
        BitConverter.GetBytes(0x2250500A008230A4UL).CopyTo(tacBytes, profile.TeamTactics.FlagsA);
        BitConverter.GetBytes(0x0000000000000064UL).CopyTo(tacBytes, profile.TeamTactics.FlagsB);

        var reader = new TeamTacticalUnderstanding(memory, profile, module);
        var options = reader.TacticOptions(team);
        Assert.NotNull(options);
        Assert.Equal([19, 17, 10, 17, 20, 12], Assert.Single(options!));
        Assert.Null(reader.TacticOptions(missingTeam));
    }

    /// <summary>Lists 0-5 written 11, 22, 33, 44, 55, 66 on Steam 27.1.0 macOS; the values are what FMRTE showed for each bar.</summary>
    [Theory]
    [InlineData("Passing Style", 44)]
    [InlineData("Build Up", 55)]
    [InlineData("Chance Creation", 66)]
    [InlineData("Line of Engagement", 11)]
    [InlineData("Pressing Intensity", 22)]
    [InlineData("Pressing Trap", 33)]
    public void Each_bar_reads_the_list_fmrte_showed(string bar, int shown)
    {
        int[] written = [11, 22, 33, 44, 55, 66];
        Assert.Equal(shown, written[TeamTacticalUnderstanding.Bars.Single(b => b.Name == bar).List]);
    }

    [Fact]
    public void Reads_ai_team_using_each_players_highest_learned_option_and_average_roles()
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        var memory = new FakeMemory();
        const ulong module = 0x1_0000_0000, aiTeam = 0x2_0000_0800;
        const ulong p1Addr = 0x4_0000_0000, p2Addr = 0x4_0001_0000;
        const ulong h1 = 0x4_0002_0000, h2 = 0x4_0003_0000;
        const ulong vecBase = 0x4_0010_0000;

        var tu = profile.TacticalUnderstanding!;
        uint playerVt = profile.PlayerVtableRvas[0];
        var p1Bytes = memory.Map(p1Addr, 0x400);
        var p2Bytes = memory.Map(p2Addr, 0x400);
        BitConverter.GetBytes(module + playerVt).CopyTo(p1Bytes, 0);
        BitConverter.GetBytes(module + playerVt).CopyTo(p2Bytes, 0);
        BitConverter.GetBytes(h1).CopyTo(p1Bytes, tu.Holder);
        BitConverter.GetBytes(h2).CopyTo(p2Bytes, tu.Holder);

        var h1Bytes = memory.Map(h1, 0xE0);
        var h2Bytes = memory.Map(h2, 0xE0);
        for (int area = 0; area < TeamTacticalUnderstanding.Areas; area++)
        {
            ulong b1 = vecBase + (ulong)(area * 0x100), b2 = b1 + 0x80;
            // Player 1 has two options: (area + 1) at (area + 1) * 10% and (area + 2) at (area + 2) * 10% -> max is (area + 2) * 10%
            var d1 = memory.Map(b1, 8);
            BitConverter.GetBytes(((uint)(area + 1) << 14) | (uint)((area + 1) * 1000)).CopyTo(d1, 0);
            BitConverter.GetBytes(((uint)(area + 2) << 14) | (uint)((area + 2) * 1000)).CopyTo(d1, 4);
            BitConverter.GetBytes(b1).CopyTo(h1Bytes, area * tu.Stride);
            BitConverter.GetBytes(b1 + 8).CopyTo(h1Bytes, area * tu.Stride + 8);
            BitConverter.GetBytes(b1 + 8).CopyTo(h1Bytes, area * tu.Stride + 16);

            // Player 2 has one option at (area + 4) * 10%
            var d2 = memory.Map(b2, 4);
            BitConverter.GetBytes(((uint)(area + 1) << 14) | (uint)((area + 4) * 1000)).CopyTo(d2, 0);
            BitConverter.GetBytes(b2).CopyTo(h2Bytes, area * tu.Stride);
            BitConverter.GetBytes(b2 + 4).CopyTo(h2Bytes, area * tu.Stride + 8);
            BitConverter.GetBytes(b2 + 4).CopyTo(h2Bytes, area * tu.Stride + 16);
        }

        // Roles: Player 1 has 20% and 40% (mean 30%), Player 2 has 50% (mean 50%) -> AI team average = 40%
        ulong r1 = vecBase + 0x1000, r2 = vecBase + 0x1100;
        var r1Bytes = memory.Map(r1, tu.RoleEntry * 2);
        BitConverter.GetBytes((ushort)2000).CopyTo(r1Bytes, tu.RoleValue);
        BitConverter.GetBytes((ushort)4000).CopyTo(r1Bytes, tu.RoleEntry + tu.RoleValue);
        BitConverter.GetBytes(r1).CopyTo(h1Bytes, tu.RoleVector);
        BitConverter.GetBytes(r1 + (ulong)(tu.RoleEntry * 2)).CopyTo(h1Bytes, tu.RoleVector + 8);

        var r2Bytes = memory.Map(r2, tu.RoleEntry);
        BitConverter.GetBytes((ushort)5000).CopyTo(r2Bytes, tu.RoleValue);
        BitConverter.GetBytes(r2).CopyTo(h2Bytes, tu.RoleVector);
        BitConverter.GetBytes(r2 + (ulong)tu.RoleEntry).CopyTo(h2Bytes, tu.RoleVector + 8);

        PlayerRecord MakePlayer(ulong addr, int id) => new()
        {
            Address = addr,
            VtableRva = playerVt,
            Id = id,
            PersonIndex = id,
            CommonName = $"P{id}",
            CurrentAbility = 100,
            PotentialAbility = 100,
            HomeReputation = 50,
            CurrentReputation = 50,
            WorldReputation = 50,
            Height = 180,
            PlayingTeamAddress = aiTeam,
            RawAttributes = [],
            AttributeScale = 5,
            Positions = [],
            Personality = [],
        };

        var squad = new[] { MakePlayer(p1Addr, 1), MakePlayer(p2Addr, 2) };
        var reader = new TeamTacticalUnderstanding(memory, profile, module);
        Assert.Equal([30, 40, 50, 60, 70, 80], reader.Read(aiTeam, squad)!);
        Assert.Equal(40, reader.ReadRoles(squad, aiTeam));
    }
}
