using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

public class RcaCalculatorTests
{
    static PlayerRecord MakePlayer(byte rawValue, params (PlayerPosition Pos, byte Rating)[] posRatings)
    {
        var raw = new byte[64];
        Array.Fill(raw, rawValue);
        raw[(int)PlayerAttribute.RightFoot] = 100;
        var pos = new byte[14];
        foreach (var (p, r) in posRatings) pos[(int)p] = r;
        return new PlayerRecord
        {
            Address = 0x2000,
            VtableRva = 1,
            Id = 200,
            CurrentAbility = 100,
            PotentialAbility = 150,
            HomeReputation = 100,
            CurrentReputation = 100,
            WorldReputation = 100,
            Height = 180,
            RawAttributes = raw,
            AttributeScale = 5,
            Positions = pos,
            Personality = new byte[8],
        };
    }

    [Fact]
    public void UniformRawAttributesMatchFormulaForNaturalStriker()
    {
        // The zero difference at 80 takes one downward step. At 79 the correction rounds to zero.
        Assert.Equal(79, RcaCalculator.Calculate(MakePlayer(50, (PlayerPosition.Striker, 20))));
        Assert.Equal(199, RcaCalculator.Calculate(MakePlayer(80, (PlayerPosition.Striker, 20))));
        // Clamp extremes
        Assert.Equal(1, RcaCalculator.Calculate(MakePlayer(1, (PlayerPosition.Striker, 20))));
        Assert.Equal(200, RcaCalculator.Calculate(MakePlayer(100, (PlayerPosition.Striker, 20))));
    }

    [Fact]
    public void Uses_the_highest_position_score_after_diversity_and_familiarity()
    {
        var multi = MakePlayer(50,
            (PlayerPosition.Goalkeeper, 20),
            (PlayerPosition.Striker, 18),
            (PlayerPosition.AttackingMidCentral, 15),
            (PlayerPosition.MidfielderCentral, 12));
        // GK paired with outfield positions puts diversity at its cap. The reference deducts floor(score / 4):
        // at 105 this yields the same effective score of 79 as for a uniform single position.
        var result = RcaCalculator.Analyze(multi);
        Assert.Equal(105, result.BestRca);
        Assert.All(result.Positions, position => Assert.Equal(105, position.UnweightedBase));
        Assert.Equal(105, result.Positions.Single(p => p.Position == PlayerPosition.Striker).Contribution);
        Assert.Equal(101, result.Positions.Single(p => p.Position == PlayerPosition.AttackingMidCentral).Contribution);
        Assert.Equal(92, result.Positions.Single(p => p.Position == PlayerPosition.MidfielderCentral).Contribution);
        Assert.Equal(1, RcaCalculator.Calculate(MakePlayer(50, (PlayerPosition.Striker, 1))));
    }

    [Fact]
    public void Either_side_can_be_the_weaker_foot_when_the_other_is_perfect()
    {
        var pLeftWeak = MakePlayer(50, (PlayerPosition.Striker, 20));
        pLeftWeak.RawAttributes[(int)PlayerAttribute.LeftFoot] = 40;
        pLeftWeak.RawAttributes[(int)PlayerAttribute.RightFoot] = 100;

        var pRightWeak = MakePlayer(50, (PlayerPosition.Striker, 20));
        pRightWeak.RawAttributes[(int)PlayerAttribute.LeftFoot] = 100;
        pRightWeak.RawAttributes[(int)PlayerAttribute.RightFoot] = 40;

        var pBothStrong = MakePlayer(50, (PlayerPosition.Striker, 20));
        pBothStrong.RawAttributes[(int)PlayerAttribute.LeftFoot] = 100;
        pBothStrong.RawAttributes[(int)PlayerAttribute.RightFoot] = 100;

        Assert.Equal(RcaCalculator.Calculate(pLeftWeak), RcaCalculator.Calculate(pRightWeak));
        Assert.True(RcaCalculator.Calculate(pBothStrong) > RcaCalculator.Calculate(pLeftWeak));
    }

    [Fact]
    public void Includes_regular_attributes_at_raw_100_and_ignores_zero_weight_attributes()
    {
        var player = MakePlayer(50, (PlayerPosition.Striker, 20));
        player.RawAttributes[(int)PlayerAttribute.Determination] = 100;
        Assert.Equal(79, RcaCalculator.Calculate(player));
        player.RawAttributes[(int)PlayerAttribute.Finishing] = 100;
        // Independently calculated by the native reference translation for this synthetic striker.
        Assert.Equal(99, RcaCalculator.Calculate(player));
    }

    [Fact]
    public void StaffRcaUsesStoredBytesRoleRatingsAndTheDefaultRoles()
    {
        var staff = new StaffRecord
        {
            Address = 0x3000, StaffAddress = 0x2F00,
            Attributes = new byte[64], Personality = new byte[8], Kind = new StaffKind(1, 2, 0x100, null, true),
        };
        Assert.Null(StaffRcaCalculator.Calculate(staff).Score);

        var block = new byte[52];
        Array.Fill(block, (byte)80);
        var fields = new Dictionary<StaffAttribute, (int Index, int Scale)>();
        var manager = new byte[15];
        Array.Fill(manager, (byte)1);
        manager[(int)StaffRole.Manager] = 20;
        var analyst = new byte[15];
        Array.Fill(analyst, (byte)1);
        analyst[(int)StaffRole.DataAnalyst] = 15;

        // Expected values come from the separate Python translation of the game's function.
        Assert.Equal((179, StaffRole.Manager), Result(manager));
        Assert.Equal((120, StaffRole.DataAnalyst), Result(analyst));
        Assert.Equal((93, StaffRole.Coach), Result(null));

        (int?, StaffRole?) Result(byte[]? roles)
        {
            var result = StaffRcaCalculator.Calculate(staff with { RcaInput = new(block, roles, fields) });
            return (result.Score, result.PrimaryRole);
        }
    }
}
