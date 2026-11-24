using Fm27Editor.Core.Game;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Competitions;
using Fm27Editor.Core.Diagnostics;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public static class UserMessage
{
    /// <summary>The short text shown to the player. The full exception goes to the log, so a bug report shows where it failed.</summary>
    public static string For(Exception error)
    {
        FlightRecorder.Current?.Log("error", error.ToString());
        return Text(error);
    }

    /// <summary>Why two clubs cannot be swapped, in words the player can act on.</summary>
    public static string ClubSwapRefusal(ClubSwapRefusedException error) => error.Reason switch
    {
        Core.Competitions.ClubSwapRefusal.SeasonStarted => Loc.F("{0} has already played its first round, so its clubs cannot be swapped this season.", error.Subject),
        Core.Competitions.ClubSwapRefusal.DifferentCountry => Loc.S("Choose a club from the same country."),
        Core.Competitions.ClubSwapRefusal.SameDivision => Loc.F("Both clubs play in {0}. Choose a club from another division.", error.Subject),
        Core.Competitions.ClubSwapRefusal.NoSeniorTeam => Loc.F("{0} has no senior team in a league.", error.Subject),
        Core.Competitions.ClubSwapRefusal.NoTable => Loc.F("{0} has no league table yet, so its clubs cannot be swapped.", error.Subject),
        Core.Competitions.ClubSwapRefusal.FixturesIncomplete => Loc.F("The fixture list of {0} is not complete yet.", error.Subject),
        Core.Competitions.ClubSwapRefusal.PointsAdjustment => Loc.F("{0} has a points adjustment and cannot be swapped.", error.Subject),
        _ => Loc.S("Choose two different clubs."),
    };

    static string Text(Exception error) => error switch
    {
        ClubSwapRefusedException ex => ClubSwapRefusal(ex),
        GameNotRunningException ex => Loc.S(ex.Message),
        SaveChangedException ex => Loc.S(ex.Message),
        UnsupportedBuildException => Loc.S("This game version is not supported"),
        PartialClauseWriteException => Loc.S("Some clause fields may have been saved. Reload before retrying."),
        GameNotReadyException => Loc.T("The game is loading a save or has no save loaded. Wait until it is in game, then press Load Game.",
            "FM đang tải save hoặc chưa có save. Chờ vào game rồi nhấn Load Game."),
        StaleObjectException => Loc.S("Game data changed. Reload before retrying."),
        MemoryAccessException => Loc.S("Couldn't access game data. Check that a save is loaded and retry."),
        AggregateException => Loc.S("The edit failed and could not be fully restored. Reload your backup save before continuing."),
        OverflowException or FormatException => Loc.S("Enter a valid number within the allowed range"),
        ArgumentOutOfRangeException => Loc.S("Value is outside the allowed range"),
        System.ComponentModel.Win32Exception ex => Loc.F("Windows error {0}: {1}", ex.NativeErrorCode, ex.Message),
        _ => Loc.VietnameseTable.ContainsKey(error.Message) ? Loc.S(error.Message)
            : Loc.IsVi ? Loc.S("The operation could not be completed. See the diagnostic log for details.") : error.Message
    };
}
