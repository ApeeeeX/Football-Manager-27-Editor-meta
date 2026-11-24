using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

public class StaffRcaCalculatorTests
{
    /// <summary>Stored bytes read from a live 27.1.0+2505117 save; expected values are the game's In-Game Editor screen for each person.</summary>
    public static TheoryData<string, int, StaffRole, byte[], byte[]> GameValues => new()
    {
        {"Craig Mawson", 151, StaffRole.GoalkeepingCoach, new byte[] { 8, 7, 9, 10, 18, 1, 12, 1, 15, 14, 15, 1, 14, 90, 2, 16, 14, 15, 11, 11, 19, 3, 12, 12, 7, 4, 30, 100, 53, 63, 78, 90, 10, 22, 40, 35, 15, 53, 40, 40, 50, 73, 78, 75, 5, 75, 70, 10, 35, 75, 30, 15 }, new byte[] { 10, 10, 10, 10, 10, 20, 1, 1, 1, 5, 5, 10, 1, 1, 5 } },
        {"Mario Cecchi", 160, StaffRole.Coach, new byte[] { 9, 6, 3, 6, 14, 11, 11, 18, 3, 9, 3, 9, 16, 81, 12, 8, 12, 16, 7, 18, 6, 6, 10, 6, 17, 11, 15, 5, 63, 63, 58, 76, 15, 58, 53, 100, 63, 63, 68, 100, 15, 75, 55, 60, 5, 30, 75, 15, 25, 43, 43, 80 }, new byte[] { 5, 10, 20, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 } },
        {"Mike Clegg", 167, StaffRole.FitnessCoach, new byte[] { 19, 1, 18, 18, 18, 7, 1, 11, 10, 1, 20, 1, 3, 90, 13, 10, 10, 17, 13, 14, 14, 6, 11, 3, 14, 15, 25, 5, 50, 60, 35, 90, 25, 38, 15, 15, 100, 25, 15, 15, 25, 10, 75, 65, 15, 40, 75, 20, 15, 75, 50, 20 }, new byte[] { 10, 10, 10, 10, 10, 1, 20, 1, 1, 5, 5, 10, 1, 12, 5 } },
        {"Gareth Whalley", 169, StaffRole.LoanManager, new byte[] { 13, 6, 6, 14, 14, 19, 13, 17, 14, 8, 20, 4, 14, 61, 5, 10, 7, 6, 13, 14, 6, 5, 5, 13, 16, 19, 40, 5, 90, 100, 100, 71, 10, 56, 45, 25, 45, 45, 45, 45, 25, 45, 60, 75, 35, 75, 75, 15, 30, 90, 30, 40 }, new byte[] { 1, 6, 16, 1, 1, 1, 1, 1, 1, 1, 1, 20, 1, 1, 1 } },
        {"Jason Wilcox", 180, StaffRole.DirectorOfFootball, new byte[] { 7, 8, 13, 11, 7, 10, 10, 3, 12, 10, 20, 6, 20, 76, 4, 12, 8, 6, 14, 5, 15, 18, 5, 1, 14, 15, 5, 5, 100, 100, 90, 61, 15, 61, 70, 60, 35, 70, 60, 50, 10, 35, 65, 50, 25, 75, 65, 20, 10, 100, 90, 25 }, new byte[] { 1, 1, 15, 1, 1, 1, 1, 1, 1, 20, 20, 1, 1, 1, 20 } },
        {"Steve Holland", 171, StaffRole.Coach, new byte[] { 20, 15, 10, 3, 19, 14, 17, 2, 15, 9, 15, 1, 18, 76, 9, 2, 17, 2, 10, 5, 4, 10, 14, 12, 15, 15, 5, 30, 86, 95, 96, 86, 20, 93, 70, 75, 40, 70, 70, 71, 15, 75, 65, 40, 35, 60, 75, 10, 85, 60, 40, 60 }, new byte[] { 15, 17, 20, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 } },
        {"Ed Leng", 146, StaffRole.SportsScientist, new byte[] { 15, 14, 9, 12, 9, 9, 6, 9, 15, 8, 15, 1, 2, 81, 10, 9, 13, 12, 10, 13, 5, 4, 5, 13, 19, 18, 30, 25, 51, 39, 52, 51, 81, 55, 45, 45, 67, 45, 45, 55, 30, 35, 5, 75, 20, 75, 15, 90, 95, 75, 60, 45 }, new byte[] { 1, 1, 1, 1, 1, 1, 20, 1, 1, 1, 1, 1, 1, 20, 1 } },
        {"Matt Konopinski", 146, StaffRole.Physio, new byte[] { 14, 17, 12, 10, 11, 11, 4, 6, 14, 9, 6, 6, 3, 90, 13, 7, 12, 11, 13, 3, 11, 1, 16, 14, 20, 10, 75, 25, 60, 60, 90, 68, 100, 22, 25, 25, 25, 25, 25, 23, 30, 40, 30, 65, 40, 5, 75, 43, 45, 33, 43, 25 }, new byte[] { 1, 1, 1, 20, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 } },
        {"Sam Williams", 170, StaffRole.Scout, new byte[] { 13, 10, 2, 14, 5, 9, 10, 5, 19, 10, 12, 4, 9, 90, 3, 13, 18, 7, 12, 19, 17, 2, 3, 16, 16, 12, 45, 25, 100, 100, 20, 40, 15, 90, 40, 35, 40, 40, 40, 52, 45, 75, 50, 20, 70, 70, 75, 10, 50, 33, 38, 40 }, new byte[] { 1, 1, 1, 1, 20, 1, 1, 1, 1, 1, 1, 1, 12, 1, 1 } },
    };

    [Theory]
    [MemberData(nameof(GameValues))]
    public void Matches_the_in_game_editor(string name, int expected, StaffRole role, byte[] block, byte[] roles)
    {
        var result = StaffRcaCalculator.Calculate(Record(block, roles));
        Assert.True(expected == result.Score, $"{name}: {result.Score}");
        Assert.Equal(role, result.PrimaryRole);
    }

    static StaffRecord Record(byte[] block, byte[] roles) => new()
    {
        Address = 0x100,
        StaffAddress = 0x200,
        Attributes = new byte[Enum.GetValues<StaffAttribute>().Length],
        Personality = new byte[8],
        Kind = new StaffKind(0, 0, 0, null, false),
        RcaInput = new(block, roles, new Dictionary<StaffAttribute, (int Index, int Scale)>()),
    };
}
