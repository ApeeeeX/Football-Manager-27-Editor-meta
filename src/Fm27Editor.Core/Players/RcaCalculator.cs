namespace Fm27Editor.Core.Players;

public sealed record PositionRca(PlayerPosition Position, int Rating, double Contribution, double UnweightedBase);

public sealed record PlayerRcaResult(int BestRca, PlayerPosition BestPosition, IReadOnlyList<PositionRca> Positions);

/// <summary>
/// Football Manager Recommended Current Ability. Scores each familiar position using the raw
/// attributes and positional diversity, then returns the highest score. CA and PA are not inputs.
/// </summary>
public static class RcaCalculator
{
    public static readonly PlayerAttribute[] AttributeOrder =
    [
        // Goalkeeping (1..11)
        PlayerAttribute.AerialReach, PlayerAttribute.CommandOfArea, PlayerAttribute.Communication,
        PlayerAttribute.Eccentricity, PlayerAttribute.Handling, PlayerAttribute.Kicking,
        PlayerAttribute.OneOnOnes, PlayerAttribute.TendencyToPunch, PlayerAttribute.Reflexes,
        PlayerAttribute.RushingOut, PlayerAttribute.Throwing,
        // Technical (12..25)
        PlayerAttribute.Corners, PlayerAttribute.Crossing, PlayerAttribute.Dribbling,
        PlayerAttribute.Finishing, PlayerAttribute.FirstTouch, PlayerAttribute.FreeKicks,
        PlayerAttribute.Heading, PlayerAttribute.LongShots, PlayerAttribute.LongThrows,
        PlayerAttribute.Marking, PlayerAttribute.Passing, PlayerAttribute.PenaltyTaking,
        PlayerAttribute.Tackling, PlayerAttribute.Technique,
        // Mental (26..39)
        PlayerAttribute.Aggression, PlayerAttribute.Anticipation, PlayerAttribute.Bravery,
        PlayerAttribute.Composure, PlayerAttribute.Concentration, PlayerAttribute.Decisions,
        PlayerAttribute.Determination, PlayerAttribute.Flair, PlayerAttribute.Leadership,
        PlayerAttribute.OffTheBall, PlayerAttribute.Positioning, PlayerAttribute.Teamwork,
        PlayerAttribute.Vision, PlayerAttribute.WorkRate,
        // Physical (40..47)
        PlayerAttribute.Acceleration, PlayerAttribute.Agility, PlayerAttribute.Balance,
        PlayerAttribute.JumpingReach, PlayerAttribute.NaturalFitness, PlayerAttribute.Pace,
        PlayerAttribute.Stamina, PlayerAttribute.Strength,
    ];

    static readonly IReadOnlyDictionary<PlayerPosition, double[]> Weights = new Dictionary<PlayerPosition, double[]>
    {
        [PlayerPosition.Goalkeeper] = [6, 6, 5, 0, 8, 5, 4, 0, 8, 0, 3, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 3, 0, 0, 1, 0, 3, 6, 2, 6, 10, 0, 0, 2, 0, 5, 2, 1, 1, 6, 8, 2, 1, 0, 3, 1, 4, 3],
        [PlayerPosition.DefenderRight] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 1, 1, 3, 1, 2, 1, 1, 3, 2, 1, 4, 2, 0, 3, 2, 2, 4, 7, 0, 0, 1, 1, 4, 2, 2, 2, 7, 6, 2, 2, 0, 5, 6, 4, 4],
        [PlayerPosition.DefenderLeft] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 1, 1, 3, 1, 2, 1, 1, 3, 2, 1, 4, 2, 0, 3, 2, 2, 4, 7, 0, 0, 1, 1, 4, 2, 2, 2, 7, 6, 2, 2, 0, 5, 6, 4, 4],
        [PlayerPosition.DefenderCentral] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 1, 5, 1, 1, 8, 2, 1, 5, 1, 0, 5, 2, 2, 4, 10, 0, 0, 2, 1, 8, 1, 1, 2, 6, 6, 2, 6, 0, 5, 3, 6, 4.5],
        [PlayerPosition.WingBackRight] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 3, 2, 1, 3, 1, 1, 1, 1, 2, 3, 1, 3, 3, 0, 3, 1, 2, 3, 5, 0, 0, 1, 2, 3, 2, 2, 2, 8, 5, 2, 1, 0, 6, 7, 4, 4],
        [PlayerPosition.WingBackLeft] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 3, 2, 1, 3, 1, 1, 1, 1, 2, 3, 1, 3, 3, 0, 3, 1, 2, 3, 5, 0, 0, 1, 2, 3, 2, 2, 2, 8, 5, 2, 1, 0, 6, 7, 4, 4],
        [PlayerPosition.DefensiveMidfielder] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 4, 1, 1, 3, 1, 3, 4, 1, 7, 3, 0, 5, 1, 2, 3, 8, 0, 0, 1, 1, 5, 2, 4, 4, 6, 6, 2, 1, 0, 4, 4, 5, 5],
        [PlayerPosition.MidfielderRight] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 5, 3, 2, 4, 1, 1, 2, 1, 1, 3, 1, 2, 4, 0, 3, 1, 3, 2, 5, 0, 0, 1, 2, 1, 2, 3, 3, 8, 6, 2, 1, 0, 6, 5, 3, 5],
        [PlayerPosition.MidfielderLeft] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 5, 3, 2, 4, 1, 1, 2, 1, 1, 3, 1, 2, 4, 0, 3, 1, 3, 2, 5, 0, 0, 1, 2, 1, 2, 3, 3, 8, 6, 2, 1, 0, 6, 5, 3, 5],
        [PlayerPosition.MidfielderCentral] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 6, 1, 1, 3, 1, 3, 6, 1, 3, 4, 0, 3, 1, 3, 2, 7, 0, 0, 1, 3, 3, 2, 6, 3, 6, 6, 2, 1, 0, 5, 6, 4, 6],
        [PlayerPosition.AttackingMidRight] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 5, 5, 2, 5, 1, 1, 2, 1, 1, 2, 1, 2, 4, 0, 3, 1, 3, 2, 5, 0, 0, 1, 2, 1, 2, 3, 3, 10, 6, 2, 1, 0, 10, 7, 3, 5.5],
        [PlayerPosition.AttackingMidLeft] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 5, 5, 2, 5, 1, 1, 2, 1, 1, 2, 1, 2, 4, 0, 3, 1, 3, 2, 5, 0, 0, 1, 2, 1, 2, 3, 3, 10, 6, 2, 1, 0, 10, 7, 3, 5.5],
        [PlayerPosition.AttackingMidCentral] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 3, 3, 5, 1, 1, 3, 1, 1, 4, 1, 2, 5, 0, 3, 1, 3, 2, 6, 0, 0, 1, 3, 2, 2, 6, 3, 9, 6, 2, 1, 0, 7, 6, 3, 7],
        [PlayerPosition.Striker] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 5, 8, 6, 1, 6, 2, 1, 1, 2, 1, 1, 4, 0, 5, 1, 6, 2, 5, 0, 0, 1, 6, 2, 1, 2, 2, 10, 6, 2, 5, 0, 7, 6, 6, 7.5],
    };

    // Indexed by PlayerPosition. These are the game's position bits, not enum ordinals.
    static readonly int[] PositionBits =
        [1, 8, 16, 4, 128, 512, 1024, 256, 4096, 8192, 2048, 16384, 64, 32];

    static readonly int[] PositionLines = [1, 0x1C, 0xE0, 0x700, 0x3800, 0x1C000];
    static readonly int[] PositionChannels = [0x11248, 0x8924, 0x6490];

    // Ratings 0 and 1 are ineligible. Ratings 18 through 20 use the full score.
    static readonly byte[] Familiarity =
        [0, 0, 1, 1, 8, 24, 40, 56, 64, 72, 80, 84, 88, 92, 94, 96, 98, 99, 100, 100, 100];

    public static int Calculate(PlayerRecord player) => Calculate(player.RawAttributes, player.Positions);

    public static int Calculate(ReadOnlySpan<byte> rawAttributes, ReadOnlySpan<byte> positions) =>
        Evaluate(rawAttributes, positions, breakdown: null).Score;

    public static PlayerRcaResult Analyze(PlayerRecord player)
    {
        ArgumentNullException.ThrowIfNull(player);
        var breakdown = new List<PositionRca>();
        var (score, bestPosition) = Evaluate(player.RawAttributes, player.Positions, breakdown);
        breakdown.Sort(static (a, b) => b.Contribution.CompareTo(a.Contribution));
        return new(score, bestPosition, breakdown);
    }

    static (int Score, PlayerPosition Position) Evaluate(
        ReadOnlySpan<byte> rawAttributes, ReadOnlySpan<byte> positions, List<PositionRca>? breakdown)
    {
        int diversity = PositionDiversity(positions);
        Span<byte> attributes = stackalloc byte[AttributeOrder.Length + 2];
        Span<double> weights = stackalloc double[attributes.Length];
        for (int i = 0; i < AttributeOrder.Length; i++)
            attributes[i] = rawAttributes[(int)AttributeOrder[i]];
        byte left = attributes[^2] = rawAttributes[(int)PlayerAttribute.LeftFoot];
        byte right = attributes[^1] = rawAttributes[(int)PlayerAttribute.RightFoot];

        int best = 1;
        var bestPosition = PlayerPosition.Goalkeeper;
        bool hasPosition = false;
        foreach (var (position, row) in Weights)
        {
            int rating = Math.Min(20, (int)positions[(int)position]);
            if (rating < 2) continue;
            row.AsSpan(0, AttributeOrder.Length).CopyTo(weights);
            // The game charges for each foot below 100. With two perfect feet it charges once.
            weights[^2] = left != 100 || right == 100 ? row[^1] : 0;
            weights[^1] = right != 100 ? row[^1] : 0;
            int baseScore = Solve(attributes, weights, diversity);
            int score = Math.Clamp((int)(baseScore * Familiarity[rating] / 100.0 + 0.5), 1, 200);
            breakdown?.Add(new(position, rating, score, baseScore));
            if (!hasPosition || score > best)
            {
                hasPosition = true;
                best = score;
                bestPosition = position;
            }
        }
        return (best, bestPosition);
    }

    static int PositionDiversity(ReadOnlySpan<byte> positions)
    {
        int mask = 0, ratingsAboveTen = 0;
        for (int i = 0; i < PositionBits.Length; i++)
        {
            if (positions[i] >= 15) mask |= PositionBits[i];
            ratingsAboveTen += Math.Max(0, positions[i] - 10);
        }

        bool goalkeeper = (mask & 1) != 0;
        bool defence = (mask & 0x1C) != 0;
        bool striker = (mask & 0x1C000) != 0;
        int midfieldLines = ((mask & 0xE0) != 0 ? 1 : 0) + ((mask & 0x700) != 0 ? 1 : 0) + ((mask & 0x3800) != 0 ? 1 : 0);
        int lines = CountOccupied(mask, PositionLines);
        int channels = CountOccupied(mask, PositionChannels);
        int diversity = 3 * (lines + channels) + ratingsAboveTen;
        if (midfieldLines > 1) diversity -= 3;
        if ((mask & 0x19B6C) != 0) diversity -= 3;

        int tier = (goalkeeper ? 1 : 0) + (defence ? 1 : 0) + (striker ? 1 : 0) + (midfieldLines > 0 ? 1 : 0);
        if (goalkeeper && tier > 1) tier += 2;
        if (defence && striker) tier = Math.Max(3, tier);
        if ((mask & 0x3800) != 0 && (mask & 0xE0) != 0) tier = Math.Max(2, tier);
        if (tier > 1) diversity += tier * tier * (tier + 1);
        return Math.Clamp(diversity, 40, 140) - 40;
    }

    static int CountOccupied(int mask, ReadOnlySpan<int> groups)
    {
        int count = 0;
        foreach (int group in groups)
            if ((mask & group) != 0) count++;
        return count;
    }

    static int Solve(ReadOnlySpan<byte> attributes, ReadOnlySpan<double> weights, int diversity)
    {
        double allWeights = 0, presentWeights = 0, numerator = 0;
        int weightCount = 0, presentCount = 0;
        Span<int> present = stackalloc int[weights.Length];
        for (int i = 0; i < weights.Length; i++)
        {
            double weight = weights[i];
            if (weight == 0) continue;
            allWeights += weight;
            weightCount++;
            if (attributes[i] == 0) continue;
            presentWeights += weight;
            numerator += weight * attributes[i];
            present[presentCount++] = i;
        }
        if (presentWeights == 0) return 1;

        double threshold = 3.75 * allWeights / weightCount;
        int score = Math.Max(1, (int)((numerator / presentWeights / 5 - 6) * 20));
        int previousStep = 0;
        for (int iteration = 1; iteration <= 100; iteration++)
        {
            int deduction = Math.Min(score >> 2, diversity / 3);
            // The game computes the reference on the 1–20 scale in single precision.
            double reference = 5.0 * ((score - deduction) / 20f + 6f);
            double excess = 0, shortfall = 0;
            foreach (int i in present[..presentCount])
            {
                double deviation = (attributes[i] - reference) * weights[i] / 5;
                excess += Math.Max(deviation - threshold, 0);
                shortfall += Math.Max(-deviation - threshold, 0);
            }

            double difference = reference - (numerator + 3.75 * (excess - shortfall)) / presentWeights;
            double rounded = difference + Math.CopySign(0.5, difference);
            int step = (int)rounded;
            if (step == 0 && rounded > 0) step = 1;
            int cap = 5 - Math.Min(4, iteration / 10);
            step = Math.Clamp(step, -cap, cap);
            if (step != 0 && previousStep != 0)
                step = DampenStep(step, previousStep);
            if (step == 0) break;
            previousStep = step;
            score -= Math.Sign(step);
        }
        return score;
    }

    static int DampenStep(int step, int previous)
    {
        if (Math.Abs(step - previous) >= 6 && Math.Abs(step) >= 2) step /= 2;
        if (Math.Abs(previous) < Math.Abs(step)) step = Math.Sign(step) * Math.Abs(previous);
        if (previous + step == 0 && Math.Abs(step) > 1) step -= Math.Sign(step);
        return previous < 0 ? Math.Max(step, previous) : Math.Min(step, previous);
    }
}
