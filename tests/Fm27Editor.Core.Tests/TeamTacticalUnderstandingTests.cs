using Fm27Editor.Core.Players;
using Xunit;

namespace Fm27Editor.Core.Tests;

public class TeamTacticalUnderstandingTests
{
    /// <summary>Man Utd tactic on Steam 27.1.0 macOS, 23/11/2026: after the game's Maximise, all 28 players held these options at 100%.</summary>
    [Fact]
    public void Options_match_what_the_game_maximised()
        => Assert.Equal([14, 20, 1, 17, 20, 4], TeamTacticalUnderstanding.OptionsOf(0x28A05008400330A4, 0x00000000000A8162));

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
}
