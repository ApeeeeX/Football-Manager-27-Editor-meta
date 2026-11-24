using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;
using static Fm27Editor.Core.Staff.StaffAttribute;

namespace Fm27Editor.Core.Staff;

public enum BackroomDepartment { Coaching, Recruitment, Medical }

/// <summary>One raise of CA or PA to the staff member's new recommended ability.</summary>
public sealed record BackroomAbilityChange(StaffRecord Staff, bool Potential, int Before, int After);

public sealed record BackroomTeamPlan(IReadOnlyList<StaffAttributeMassChange> Changes, IReadOnlyList<(StaffRecord Staff, string Reason)> Skipped)
{
    public IReadOnlyList<BackroomAbilityChange> Abilities { get; init; } = [];
}

/// <summary>Raises one team's staff to role-specific minimums and CA/PA to the resulting RCA without changing their job or contract. Never lowers a value.</summary>
public static class BackroomTeam
{
    sealed record Preset(BackroomDepartment Department, StaffAttribute[] Primary, StaffAttribute[] Secondary);

    static readonly Preset Manager = new(BackroomDepartment.Coaching, [PeopleManagement, TacticalKnowledge, TacticalAttacking], [Authority, Motivating, JudgingPlayerAbility]);
    static readonly Preset Assistant = new(BackroomDepartment.Coaching, [PeopleManagement, JudgingPlayerAbility, JudgingPlayerPotential], [Determination, Authority, Motivating, TacticalKnowledge]);
    static readonly Preset Coach = new(BackroomDepartment.Coaching, [CoachingTechnical, CoachingTactical, CoachingPlayer], [Determination, Authority, Motivating]);
    static readonly Preset Youth = new(BackroomDepartment.Coaching, [WorkingWithYoungsters, JudgingPlayerPotential], [PeopleManagement, CoachingTechnical, Determination]);
    static readonly Preset Fitness = new(BackroomDepartment.Coaching, [CoachingFitness], [Determination, Authority, Motivating]);
    static readonly Preset Goalkeeping = new(BackroomDepartment.Coaching, [CoachingGoalkeeping], [Determination, Authority, Motivating]);
    static readonly Preset SetPiece = new(BackroomDepartment.Coaching, [SetPieces, TacticalKnowledge], [Determination, Authority, Motivating]);
    static readonly Preset Director = new(BackroomDepartment.Recruitment, [JudgingPlayerAbility, JudgingPlayerPotential, Negotiating], [JudgingStaffAbility, PeopleManagement]);
    static readonly Preset TechnicalDirector = new(BackroomDepartment.Recruitment, [JudgingStaffAbility, Negotiating], [PeopleManagement, JudgingPlayerAbility]);
    static readonly Preset Scout = new(BackroomDepartment.Recruitment, [JudgingPlayerAbility, JudgingPlayerPotential], [Determination, TacticalKnowledge]);
    static readonly Preset Analyst = new(BackroomDepartment.Recruitment, [JudgingPlayerData, JudgingTeamData, PresentingData], [TacticalKnowledge]);
    static readonly Preset PerformanceAnalyst = new(BackroomDepartment.Coaching, [JudgingPlayerData, JudgingTeamData, PresentingData], [TacticalKnowledge]);
    static readonly Preset Loan = new(BackroomDepartment.Recruitment, [JudgingPlayerPotential, PeopleManagement], [JudgingPlayerAbility, Negotiating]);
    static readonly Preset Physio = new(BackroomDepartment.Medical, [Physiotherapy], [Determination, PeopleManagement]);
    static readonly Preset Science = new(BackroomDepartment.Medical, [SportsScience], [Physiotherapy, Determination]);

    static Preset? FindPreset(StaffRecord staff) => staff.Kind.Manager ? null : staff.JobCode switch
    {
        16 or 17 or 52 or 144 => Manager,
        20 or 21 => Assistant,
        2 or 3 => Coach,
        64 => Youth,
        26 or 27 => Fitness,
        34 or 35 => Goalkeeping,
        22 => SetPiece,
        10 => Director,
        88 or 89 => TechnicalDirector,
        14 or 44 or 45 => Scout,
        58 => Analyst,
        60 or 61 or 62 => PerformanceAnalyst,
        86 => Loan,
        12 or 38 or 46 or 50 => Physio,
        40 or 48 => Science,
        _ => null,
    };

    /// <summary>Equally weak areas are filled in this order, which reaches defending, attacking and possession early.</summary>
    static readonly TrainingCategory[] OutfieldAreas =
    [
        TrainingCategory.DefendingTactical, TrainingCategory.AttackingTechnical, TrainingCategory.PossessionTactical,
        TrainingCategory.DefendingTechnical, TrainingCategory.AttackingTactical, TrainingCategory.PossessionTechnical,
    ];

    static Preset AreaPreset(TrainingCategory area) => new(BackroomDepartment.Coaching,
        TrainingRatings.Weights(area).Select(weight => weight.Attribute).ToArray(), [Determination, Authority, Motivating]);

    static int Affinity(StaffRecord coach, TrainingCategory area) =>
        TrainingRatings.Weights(area).Sum(weight => coach.Attribute(weight.Attribute) * weight.Weight);

    static int Minimum(Preset preset, StaffAttribute attribute) =>
        preset.Primary.Contains(attribute) ? 20 : preset.Secondary.Contains(attribute) ? 18 : 0;

    /// <summary>Gives each general coach the training area the team is weakest in, counting the raises already handed
    /// out, and picks the coach closest to that area. A coach rated for set pieces takes that area when the team has no
    /// set-piece job, and is then not counted for the outfield areas.</summary>
    internal static Dictionary<int, TrainingCategory> AssignCoachAreas(IReadOnlyList<StaffRecord> people)
    {
        var assigned = new Dictionary<int, TrainingCategory>();
        foreach (var team in people.GroupBy(person => person.TeamAddress))
        {
            var open = team.Where(person => person.JobCode is 2 or 3).OrderBy(person => person.Id).ToList();
            if (!team.Any(person => person.JobCode == 22) && open.Count > 0)
            {
                var candidate = open.OrderByDescending(coach => coach.Roles?[(int)StaffRole.SetPieceCoach] ?? 0)
                    .ThenByDescending(coach => Affinity(coach, TrainingCategory.SetPieces)).First();
                if ((candidate.Roles?[(int)StaffRole.SetPieceCoach] ?? 0) >= 15 || open.Count > OutfieldAreas.Length)
                {
                    assigned[candidate.Id] = TrainingCategory.SetPieces;
                    open.Remove(candidate);
                }
            }
            var pool = open.ToArray();
            while (open.Count > 0)
            {
                var weakest = OutfieldAreas.MinBy(area => pool.Max(coach => TrainingRatings.Score(area, attribute => Raised(coach, attribute))));
                var chosen = open.MaxBy(coach => Affinity(coach, weakest))!;
                assigned[chosen.Id] = weakest;
                open.Remove(chosen);
            }

            int Raised(StaffRecord coach, StaffAttribute attribute)
            {
                int value = coach.Attribute(attribute);
                return value > 0 && assigned.TryGetValue(coach.Id, out var area) ? Math.Max(value, Minimum(AreaPreset(area), attribute)) : value;
            }
        }
        return assigned;
    }

    public static BackroomDepartment? DepartmentOf(StaffRecord staff) => FindPreset(staff)?.Department;

    /// <summary>The head coach of the first team is left alone; the managers of the second and third teams are not.
    /// A manager whose team cannot be told apart from the first team counts as its head coach.</summary>
    public static bool IsEligible(StaffRecord staff, ulong firstTeam) =>
        FindPreset(staff) is not null &&
        !(staff.JobCode is 16 or 144 && (firstTeam == 0 || staff.TeamAddress == 0 || staff.TeamAddress == firstTeam));

    public static BackroomTeamPlan Plan(IEnumerable<StaffRecord> staff, ulong club, BackroomDepartment? department, StaffWriter writer,
        ulong? team = null, ulong firstTeam = 0)
    {
        var changes = new List<StaffAttributeMassChange>();
        var skipped = new List<(StaffRecord, string)>();
        var abilities = new List<BackroomAbilityChange>();
        var people = staff.Where(person => person.Club?.Address == club && person.Id > 0 && (team is not { } only || person.TeamAddress == only))
            .DistinctBy(person => person.Id).Where(person => IsEligible(person, firstTeam)).ToList();
        var coachAreas = AssignCoachAreas(people);
        foreach (var person in people)
        {
            var preset = coachAreas.TryGetValue(person.Id, out var coachArea) ? AreaPreset(coachArea) : FindPreset(person)!;
            if (department.HasValue && preset.Department != department) continue;
            var ratings = preset.Primary.Select(a => (Attribute: a, Minimum: 20)).Concat(preset.Secondary.Select(a => (Attribute: a, Minimum: 18)));
            var planned = new List<StaffAttributeMassChange>();
            try
            {
                foreach (var (attribute, minimum) in ratings)
                {
                    // Some jobs have blank attributes. Preserve those fields rather than invent a before value.
                    if (person.Attribute(attribute) is > 0 && person.Attribute(attribute) < minimum)
                        planned.AddRange(StaffAttributeMassEdit.Plan([person], attribute, MassEditMode.Set, minimum, writer));
                }
                changes.AddRange(planned);
                // CA at or above the new RCA keeps the raised attributes; an unknown RCA leaves CA and PA alone.
                if (StaffRcaCalculator.Calculate(person, planned.ToDictionary(change => change.Field, change => change.After)).Score is { } recommended)
                {
                    if (person.CurrentAbility < recommended) abilities.Add(new(person, false, person.CurrentAbility, recommended));
                    if (person.PotentialAbility < recommended) abilities.Add(new(person, true, person.PotentialAbility, recommended));
                }
            }
            catch (Exception ex) when (ex is StaleObjectException or MemoryAccessException)
            {
                skipped.Add((person, ex.Message));
            }
        }
        return new(changes, skipped) { Abilities = abilities };
    }

    public static void Apply(StaffAttributeMassChange change, StaffRepository repository, StaffWriter writer)
    {
        var snap = change.Staff;
        if (repository.Reload(snap.StaffAddress) is not { } current || current.Id != snap.Id ||
            current.Address != snap.Address || current.Club?.Address != snap.Club?.Address || current.JobCode != snap.JobCode)
            throw new StaleObjectException("The staff member changed club or job since the preset was reviewed");
        writer.SetAttributeRaw(current, change.Field, change.BeforeRaw, change.AfterRaw);
    }

    /// <summary>Raises CA or PA, and PA with CA so CA never ends above PA; a value already higher stays.</summary>
    public static void Apply(BackroomAbilityChange change, StaffRepository repository, StaffWriter writer)
    {
        var snap = change.Staff;
        if (repository.Reload(snap.StaffAddress) is not { } current || current.Id != snap.Id ||
            current.Address != snap.Address || current.Club?.Address != snap.Club?.Address || current.JobCode != snap.JobCode)
            throw new StaleObjectException("The staff member changed club or job since the preset was reviewed");
        int ca = current.CurrentAbility, pa = current.PotentialAbility;
        if (change.Potential) pa = Math.Max(pa, change.After);
        else { ca = Math.Max(ca, change.After); pa = Math.Max(pa, ca); }
        if (ca != current.CurrentAbility || pa != current.PotentialAbility) writer.SetAbility(current, ca, pa);
    }
}
