using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

/// <summary>
/// Calculates the workbook's Meta CA score from the player's displayed 1-20 attributes.
/// The five source weights per attribute cover levels 2-4, 5-8, 9-12, 13-16, and 17-20.
/// </summary>
internal static class McaCalculator
{
    public static bool IsGoalkeeper(PlayerRecord player) => player.Position(PlayerPosition.Goalkeeper) >= 15;

    readonly record struct Curve(PlayerAttribute Attribute, double[] Weights)
    {
        public double Maximum => Weights.Sum();

        public double AtLevel(int level)
        {
            double score = 0;
            for (int current = 2; current <= level; current++)
            {
                int group = current <= 4 ? 0 : (current - 1) / 4;
                score += Weights[group] / (group == 0 ? 3 : 4);
            }
            return score;
        }
    }

    // Source: user-provided workbook, "Winrate per level". Values retain the workbook's signed weights.
    static readonly Curve[] Curves =
    [
        new(PlayerAttribute.Pace, [0.055848, 0.076608, 0.090789, 0.083041, 0.060965]),
        new(PlayerAttribute.Acceleration, [0.047953, 0.074561, 0.083187, 0.078655, 0.077924]),
        new(PlayerAttribute.Stamina, [0.046784, 0.045906, 0.032602, 0.074708, 0.024854]),
        new(PlayerAttribute.JumpingReach, [0.007749, 0.025146, 0.048099, 0.044883, 0.056579]),
        new(PlayerAttribute.Anticipation, [0.023246, 0.055848, 0.021491, 0.045175, 0.024415]),
        new(PlayerAttribute.Strength, [0.008041, 0.054971, 0.019883, 0.057602, -0.007749]),
        new(PlayerAttribute.Concentration, [0.010234, 0.023977, 0.026901, 0.025731, 0.028509]),
        new(PlayerAttribute.WorkRate, [0.077339, 0.015643, 0.010673, 0.010088, -0.010965]),
        new(PlayerAttribute.Dribbling, [0.021784, 0.019591, 0.009795, 0.038889, 0.001754]),
        new(PlayerAttribute.Balance, [-0.000877, 0.030409, 0.017836, 0.017105, 0.023538]),
        new(PlayerAttribute.Finishing, [0.007749, 0.005556, 0.020029, 0.027193, 0.019591]),
        new(PlayerAttribute.Agility, [0.009064, 0.034064, 0.007749, 0.008626, 0.001316]),
        new(PlayerAttribute.Vision, [0.025585, 0.002193, 0.006433, 0.001608, 0.021491]),
        new(PlayerAttribute.LongShots, [0.012281, -0.002047, 0.025877, 0.016959, 0.002924]),
        new(PlayerAttribute.Determination, [0.016959, 0.012135, 0.008626, 0.018860, -0.004094]),
        new(PlayerAttribute.LeftFoot, [0.012573, 0.012135, 0.018421, 0.001316, -0.002924]),
        new(PlayerAttribute.Composure, [-0.010819, 0.032310, 0.002924, 0.011550, 0.004678]),
        new(PlayerAttribute.Decisions, [-0.001170, 0.024415, -0.011111, 0.005556, 0.009795]),
        new(PlayerAttribute.Heading, [-0.001316, 0.029825, -0.018129, 0.014181, -0.003947]),
        new(PlayerAttribute.Passing, [0.010088, 0.003509, 0.003363, 0.002339, -0.003070]),
        new(PlayerAttribute.Marking, [0.015351, -0.009211, 0.004678, 0.002339, 0.001754]),
        new(PlayerAttribute.Positioning, [0.002632, 0.010526, -0.005409, 0.005848, -0.004825]),
        new(PlayerAttribute.Bravery, [0.010965, -0.015789, 0.005994, 0.021637, -0.015789]),
        new(PlayerAttribute.FirstTouch, [0.012281, -0.011404, -0.001901, 0.011111, -0.003509]),
        new(PlayerAttribute.Teamwork, [0.017105, -0.010965, -0.003655, -0.002924, 0.005702]),
        new(PlayerAttribute.Tackling, [-0.007018, -0.001316, -0.001023, 0.000146, 0.012281]),
        new(PlayerAttribute.Technique, [-0.013158, 0.008772, -0.005409, -0.002924, 0.010526]),
        new(PlayerAttribute.Crossing, [-0.002193, -0.006579, -0.000585, -0.009064, 0.015351]),
        new(PlayerAttribute.Aggression, [-0.003509, 0.013158, -0.008918, 0.007164, -0.014912]),
        new(PlayerAttribute.OffTheBall, [-0.005263, -0.017982, 0.002924, 0.006725, -0.007895]),
        new(PlayerAttribute.Flair, [-0.012427, -0.007018, -0.002339, 0.003216, -0.010088]),
    ];

    static readonly double PositiveMaximumPool = Curves.Sum(curve => Math.Max(0, curve.Maximum));

    public static double Calculate(PlayerRecord player)
    {
        if (IsGoalkeeper(player) || PositiveMaximumPool <= 0) return 0;

        double cumulative = 0;
        foreach (var curve in Curves)
            cumulative += curve.AtLevel(player.Attribute(curve.Attribute));

        return Math.Max(0, cumulative / PositiveMaximumPool * 200);
    }
}
