namespace Fm27Editor.Core.Staff;

/// <summary>Score is null when the record lacks the stored bytes the game's calculation reads.</summary>
public sealed record StaffRcaResult(int? Score, StaffRole? PrimaryRole, string? UnavailableReason = null);

/// <summary>The stored attribute block and role ratings behind one staff record. Null roles mean the game's default ratings.</summary>
public sealed record StaffRcaInput(byte[] Block, byte[]? Roles, IReadOnlyDictionary<StaffAttribute, (int Index, int Scale)> Fields);

/// <summary>
/// The game's Non Player Recommended Current Ability (editor field NRCA), translated from game_plugin 27.1.0+2505117
/// Steam macOS (0x4A31220, solver 0x4A31DB4, reference 0x4A32E74). Checked against nine In-Game Editor values.
/// </summary>
public static class StaffRcaCalculator
{
    const int BlockLength = 52;

    /// <summary>Non-zero (attribute block index, weight) pairs for the game's sixteen job rows; rows 9 and 11 are not scored.</summary>
    static readonly (int Index, int Weight)[][] JobWeights =
    [
        /*  0 */ [(13, 3), (28, 4), (29, 3), (30, 4), (31, 4), (33, 4), (34, 3), (35, 3), (37, 3), (38, 3), (39, 3), (49, 3), (50, 3)],
        /*  1 */ [(13, 3), (28, 4), (29, 3), (30, 4), (31, 4), (33, 3), (34, 2), (35, 2), (37, 2), (38, 2), (39, 2), (49, 1), (50, 1)],
        /*  2 */ [(13, 2), (28, 3), (29, 3), (30, 1), (31, 2), (33, 1), (34, 4), (35, 4), (36, 1), (37, 4), (38, 4), (39, 4), (49, 1), (50, 1)],
        /*  3 */ [(13, 2), (30, 2), (31, 2), (32, 4), (47, 2), (49, 1), (50, 2)],
        /*  4 */ [(13, 2), (28, 4), (29, 4), (33, 4), (49, 1), (50, 2)],
        /*  5 */ [(13, 2), (27, 4), (28, 2), (29, 2), (30, 2), (31, 3), (33, 1), (37, 2), (41, 4), (42, 4)],
        /*  6 */ [(13, 2), (30, 2), (31, 3), (36, 4)],
        /*  7 */ [(13, 2), (28, 4), (29, 4), (30, 4), (31, 2), (33, 1), (49, 4), (50, 4)],
        /*  8 */ [(13, 2), (28, 4), (29, 4), (30, 2), (31, 2), (33, 1), (34, 2), (35, 2), (36, 1), (37, 2), (38, 2), (39, 2), (49, 2), (50, 3)],
        /*  9 */ [],
        /* 10 */ [(13, 2), (28, 3), (33, 2), (44, 4)],
        /* 11 */ [],
        /* 12 */ [(13, 2), (30, 2), (31, 2), (32, 2), (36, 2), (47, 4)],
        /* 13 */ [(13, 2), (28, 4), (29, 4), (30, 4), (31, 3), (33, 2)],
        /* 14 */ [(13, 2), (28, 3), (29, 3), (30, 3), (31, 2), (33, 1), (34, 2), (35, 2), (36, 1), (37, 2), (38, 2), (39, 2), (49, 4), (50, 4)],
        /* 15 */ [(13, 2), (30, 1), (31, 2), (38, 1), (39, 2), (51, 4)],
    ];

    static readonly int[] Jobs = [0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 13, 14, 15];
    static readonly StaffRole[] JobRole =
    [
        StaffRole.Manager, StaffRole.AssistantManager, StaffRole.Coach, StaffRole.Physio, StaffRole.Scout, StaffRole.GoalkeepingCoach,
        StaffRole.FitnessCoach, StaffRole.DirectorOfFootball, StaffRole.HeadOfYouthDevelopment, StaffRole.Chairperson, StaffRole.DataAnalyst,
        StaffRole.LoanManager, StaffRole.SportsScientist, StaffRole.LoanManager, StaffRole.TechnicalDirector, StaffRole.SetPieceCoach,
    ];
    static readonly byte[] DefaultRoles = [10, 10, 10, 10, 10, 1, 1, 1, 1, 5, 5, 10, 1, 1, 5];

    public static StaffRcaResult Calculate(StaffRecord staff, IReadOnlyDictionary<StaffAttribute, int>? attributes = null,
        IReadOnlyDictionary<StaffRole, int>? roles = null)
    {
        ArgumentNullException.ThrowIfNull(staff);
        if (staff.RcaInput is not { Block.Length: BlockLength } input)
            return new(null, null, "Stored staff attributes are unavailable");

        Span<sbyte> block = stackalloc sbyte[BlockLength];
        for (int i = 0; i < BlockLength; i++) block[i] = (sbyte)input.Block[i];
        if (attributes is not null)
            foreach (var (attribute, value) in attributes)
                if (input.Fields.TryGetValue(attribute, out var field)) block[field.Index] = checked((sbyte)(value * field.Scale));

        Span<byte> ratings = stackalloc byte[DefaultRoles.Length];
        (input.Roles ?? DefaultRoles).AsSpan(0, ratings.Length).CopyTo(ratings);
        if (roles is not null)
            foreach (var (role, value) in roles) ratings[(int)role] = checked((byte)value);

        int best = 0, bestJob = -1, bestRating = 0;
        foreach (int job in Jobs)
        {
            int score = Solve(block, ratings, job), rating = ratings[(int)JobRole[job]];
            if (score > best || (score == best && rating > bestRating)) (best, bestJob, bestRating) = (score, job, rating);
        }
        return bestJob < 0 ? new(null, null, "Stored staff attributes are unavailable") : new(best, JobRole[bestJob]);
    }

    static int Solve(ReadOnlySpan<sbyte> block, ReadOnlySpan<byte> ratings, int job)
    {
        int sumWeight = 0, sum = 0;
        foreach (var (index, weight) in JobWeights[job])
            if (block[index] != 0) { sum += block[index] * weight; sumWeight += weight; }
        if (sumWeight <= 0) return 0;
        double mean = sum / (double)sumWeight;
        var role = JobRole[job];

        double subtract = job <= 1 ? 2.5 : job == 2 ? 3.0 : 0.0, multiply = job <= 1 ? 13.3 : job == 2 ? 14.3 : 10.0;
        int score = Math.Max((int)((mean / 5 - subtract) * multiply + 0.5), 1), previous = -1000;
        for (int iteration = 1; iteration <= 100; iteration++)
        {
            double difference = Math.Max(Reference(role, score) * 5, 3.0) - mean;
            double rounded = difference + (difference < 0 ? -0.5 : 0.5);
            int step = (int)rounded;
            if (step == 0 && rounded > 0.5) step = 1;
            int shrink = Math.Min(4, iteration / 10);
            step = Math.Clamp(step, shrink - 5, 5 - shrink);
            if (step != 0 && previous >= -999)
            {
                if (Math.Abs(step - previous) >= 6 && Math.Abs(step) >= 2) step /= 2;
                if (Math.Abs(previous) < Math.Abs(step)) step = step > 0 ? Math.Abs(previous) : -Math.Abs(previous);
                if (previous + step == 0 && Math.Abs(step) != 1) step += step > 0 ? -1 : 1;
                if (previous >= 1 && step > previous)
                {
                    if (--score < 1) break;
                    continue;
                }
                if (previous < 0) step = Math.Max(step, previous);
            }
            if (step == 0) break;
            previous = step;
            if (step >= 1) { if (--score < 1) break; }
            else score++;
        }
        int factor = Familiarity(ratings[(int)role]);
        return Math.Clamp((int)(factor * score / 100.0 + 0.5), 1, 200);
    }

    static double Reference(StaffRole role, int score) => role switch
    {
        StaffRole.Manager or StaffRole.AssistantManager => Math.Clamp(score / 13.3 + 2.5, 1, 20),
        StaffRole.Coach => Math.Clamp(score / 14.3 + 3.0, 1, 20),
        StaffRole.Physio or StaffRole.DataAnalyst or StaffRole.SportsScientist or StaffRole.Scout or StaffRole.GoalkeepingCoach
            or StaffRole.FitnessCoach or StaffRole.SetPieceCoach => Math.Clamp(score / 10.0, 1, 20),
        _ => score / 10.0,
    };

    static int Familiarity(int rating)
    {
        if (rating > 19) return 100;
        int factor = 100;
        for (int limit = 19; ; limit--)
        {
            factor = Math.Max(factor, 6) - 5;
            if (rating >= limit) return factor;
        }
    }
}
