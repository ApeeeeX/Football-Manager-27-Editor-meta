using Fm27Editor.Core.Players;
using Xunit;

namespace Fm27Editor.Core.Tests;

public class TeamTacticalUnderstandingTests
{
    /// <summary>Man Utd tactic on Steam 27.1.0 macOS, 23/11/2026: after the game's Maximise, all 28 players held these options at 100%.</summary>
    [Fact]
    public void Options_match_what_the_game_maximised()
        => Assert.Equal([14, 20, 1, 17, 20, 4], TeamTacticalUnderstanding.OptionsOf(0x28A05008400330A4, 0x00000000000A8162));
}
