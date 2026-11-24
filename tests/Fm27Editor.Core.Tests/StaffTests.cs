using Fm27Editor.Core.Players;
using Fm27Editor.Core.Staff;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// Staff from FM27 RAM: person vector [module + 0x5703738] + 0x88; a staff entry points at the person part (vtable
/// 0x4D8BE78) and the staff part (vtable 0x4D8BE08) sits 0x100 below it. Staff-part bytes +0x10..+0x43 and +0xD4..+0xDD
/// and person bytes +0x70 / +0x88 are Alex Ferguson's as dumped; expected values are FMRTE 27's Information tab for him
/// (Man Utd director, Unique ID 2020287, born 31/12/1941, reputation 9250/9250/8750, CA/PA 188/195).
/// </summary>
public class StaffTests
{
    const ulong Root = 0x6000_0000, Vector = 0x6000_1000, Obj = 0x6001_0000, Person = Obj + 0x100, Job = 0x6002_0000;
    readonly TestWorld _w = new();

    public StaffTests()
    {
        TestWorld.Set64(_w.Memory.Map(TestWorld.ModuleBase + 0x5703738, 8), 0, Root);
        var r = _w.Memory.Map(Root, 0xA0);
        TestWorld.Set64(r, 0x88, Vector); TestWorld.Set64(r, 0x90, Vector + 16);
        var v = _w.Memory.Map(Vector, 16);
        TestWorld.Set64(v, 0, TestWorld.Mainoo); // a player in the same vector is skipped
        TestWorld.Set64(v, 8, Person);

        var o = _w.Memory.Map(Obj, 0x240);
        TestWorld.Set64(o, 0x00, TestWorld.ModuleBase + 0x4D8BE08);
        Convert.FromHexString("0F090C08130E0E1111050F0111640F1410130C0F06120E1210124B055A5A5A5F0541553C2D6446462D2D4B4B054B2D0F2D5A5037").CopyTo(o, 0x10);
        Convert.FromHexString("222422242E22BC00C300").CopyTo(o, 0xD4);
        TestWorld.Set64(o, 0x100, TestWorld.ModuleBase + 0x4D8BE78);
        BitConverter.GetBytes(2020287).CopyTo(o, 0x10C);
        Convert.FromHexString("0C141413130B0712").CopyTo(o, 0x170);
        Convert.FromHexString("6D019507").CopyTo(o, 0x188);
        TestWorld.Set64(o, 0x1A8, Job);
        var j = _w.Memory.Map(Job, 0x28);
        TestWorld.Set64(j, 0x8, Person);
        Convert.FromHexString("D495000026000600").CopyTo(j, 0x20); // Ferguson's record: board role 38, job 6 (Director)
    }

    StaffRepository Repo() => new(_w.Memory, _w.Profile, TestWorld.ModuleBase);

    [Fact]
    public void Reads_Ferguson_as_FMRTE_shows_him()
    {
        var s = Assert.Single(Repo().LoadAll());
        Assert.Equal(2020287, s.Id);
        Assert.Equal(new DateOnly(1941, 12, 31), s.DateOfBirth);
        Assert.Equal((short)6, s.JobCode);
        Assert.Equal((9250, 9250, 8750), ((int)s.HomeReputation, (int)s.CurrentReputation, (int)s.WorldReputation));
        Assert.Equal((188, 195), ((int)s.CurrentAbility, (int)s.PotentialAbility));
        (StaffAttribute a, int v)[] expected =
        [
            (StaffAttribute.CoachingAttacking, 17), (StaffAttribute.CoachingDefending, 12), (StaffAttribute.CoachingFitness, 9),
            (StaffAttribute.CoachingGoalkeeping, 1), (StaffAttribute.CoachingPossession, 20), (StaffAttribute.CoachingPlayer, 15),
            (StaffAttribute.CoachingTactical, 14), (StaffAttribute.CoachingTechnical, 14), (StaffAttribute.PeopleManagement, 18),
            (StaffAttribute.WorkingWithYoungsters, 17), (StaffAttribute.DirtinessAllowance, 9), (StaffAttribute.Versatility, 15),
            (StaffAttribute.SetPieces, 11),
            (StaffAttribute.TacticalAttacking, 15), (StaffAttribute.Depth, 15), (StaffAttribute.Directness, 8),
            (StaffAttribute.Flamboyancy, 6), (StaffAttribute.Flexibility, 18), (StaffAttribute.FreeRoles, 14),
            (StaffAttribute.Marking, 17), (StaffAttribute.Offside, 17), (StaffAttribute.Pressing, 15), (StaffAttribute.SittingBack, 16),
            (StaffAttribute.Tempo, 16), (StaffAttribute.UseOfPlaymaker, 19), (StaffAttribute.UseOfSubstitutions, 12), (StaffAttribute.Width, 18),
            (StaffAttribute.BuyingPlayers, 15), (StaffAttribute.HardnessOfTraining, 14), (StaffAttribute.MindGames, 20),
            (StaffAttribute.SquadRotation, 18),
            (StaffAttribute.Determination, 20), (StaffAttribute.JudgingPlayerAbility, 18), (StaffAttribute.JudgingPlayerPotential, 18),
            (StaffAttribute.JudgingStaffAbility, 16), (StaffAttribute.Negotiating, 18), (StaffAttribute.Authority, 19),
            (StaffAttribute.Motivating, 19), (StaffAttribute.TacticalKnowledge, 13), (StaffAttribute.Physiotherapy, 1),
            (StaffAttribute.SportsScience, 3),
            (StaffAttribute.JudgingPlayerData, 1), (StaffAttribute.JudgingTeamData, 15), (StaffAttribute.PresentingData, 9),
            (StaffAttribute.Business, 9), (StaffAttribute.Interference, 14), (StaffAttribute.Patience, 5), (StaffAttribute.Resources, 1),
        ];
        Assert.Equal(Enum.GetValues<StaffAttribute>().Length, expected.Length);
        Assert.All(expected, e => Assert.Equal(e.v, s.Attribute(e.a)));
        Assert.Equal(new byte[] { 12, 20, 20, 19, 19, 11, 7, 18 }, s.Personality);
    }

    [Fact]
    public void Writes_scaled_and_raw_attributes_ability_and_personality_in_place()
    {
        var s = Repo().LoadAll()[0];
        var w = new StaffWriter(_w.Memory, _w.Profile, TestWorld.ModuleBase);
        w.SetAttribute(s, StaffAttribute.CoachingAttacking, 20);   // stored ×5 at staff + 0x32
        w.SetAttribute(s, StaffAttribute.TacticalAttacking, 3);    // stored 1–20 at staff + 0x10
        w.SetAbility(s, 150, 200);
        w.SetPersonality(s, StaffPersonality.Temperament, 20);
        Assert.Equal(100, _w.Bytes(Obj + 0x32, 1)[0]);
        Assert.Equal(3, _w.Bytes(Obj + 0x10, 1)[0]);
        Assert.Equal(Convert.FromHexString("9600C800"), _w.Bytes(Obj + 0xDA, 4));
        Assert.Equal(20, _w.Bytes(Person + 0x76, 1)[0]);
        var again = Repo().Reload(Person)!;
        Assert.Equal(20, again.Attribute(StaffAttribute.CoachingAttacking));
        Assert.Equal(17, again.Attribute(StaffAttribute.WorkingWithYoungsters));
        // The old snapshot no longer matches memory.
        Assert.Throws<StaleObjectException>(() => w.SetAttribute(s, StaffAttribute.CoachingAttacking, 5));
    }

    [Fact]
    public void Backroom_recruitment_preset_raises_scouting_fields_and_refuses_when_the_job_has_changed()
    {
        var repo = Repo();
        var person = Assert.Single(repo.LoadAll());
        var writer = new StaffWriter(_w.Memory, _w.Profile, TestWorld.ModuleBase);
        var club = new ClubInfo(0x9000, "Man Utd", "Man Utd");
        var scout = person with { Club = club, JobCode = 14 };
        var plan = BackroomTeam.Plan([scout], club.Address, BackroomDepartment.Recruitment, writer);
        Assert.Empty(plan.Skipped);
        Assert.Equal(3, plan.Changes.Count);
        var ability = Assert.Single(plan.Changes, change => change.Field == StaffAttribute.JudgingPlayerAbility);
        Assert.Equal((18, 20, 90, 100), (ability.Before, ability.After, (int)ability.BeforeRaw, (int)ability.AfterRaw));
        Assert.Contains(plan.Changes, change => change.Field == StaffAttribute.TacticalKnowledge && change.Before == 13 && change.After == 18);
        Assert.Empty(BackroomTeam.Plan([scout], club.Address, BackroomDepartment.Medical, writer).Changes);
        Assert.Empty(BackroomTeam.Plan([scout], 0xA000, null, writer).Changes);
        Assert.Throws<StaleObjectException>(() => BackroomTeam.Apply(ability, repo, writer));
    }

    [Fact]
    public void Backroom_builds_one_team_skips_the_first_team_head_coach_and_raises_ca_and_pa_to_the_new_rca()
    {
        const ulong First = 0x9100, U21 = 0x9200;
        var repo = Repo();
        var person = Assert.Single(repo.LoadAll());
        var writer = new StaffWriter(_w.Memory, _w.Profile, TestWorld.ModuleBase);
        var club = new ClubInfo(0x9000, "Man Utd", "Man Utd");
        // One fixture person in three jobs: the planner checks each record against memory, so the ID stays the real one.
        var block = Convert.FromHexString("0F090C08130E0E1111050F0111640F1410130C0F06120E1210124B055A5A5A5F0541553C2D6446462D2D4B4B054B2D0F2D5A5037");
        var fields = new Dictionary<StaffAttribute, (int Index, int Scale)>
        {
            [StaffAttribute.TacticalAttacking] = (0, 1), [StaffAttribute.Authority] = (4, 1), [StaffAttribute.JudgingPlayerAbility] = (28, 5),
            [StaffAttribute.PeopleManagement] = (30, 5), [StaffAttribute.Motivating] = (31, 5), [StaffAttribute.TacticalKnowledge] = (33, 5),
        };
        var low = person with { CurrentAbility = 90, PotentialAbility = 95, RcaInput = new(block, null, fields) };
        var head = low with { Club = club, JobCode = 16, TeamAddress = First };
        var reserve = low with { Club = club, JobCode = 16, TeamAddress = U21 };
        var scout = low with { Club = club, JobCode = 14, TeamAddress = First };

        Assert.False(BackroomTeam.IsEligible(head, First));
        Assert.True(BackroomTeam.IsEligible(reserve, First));
        Assert.False(BackroomTeam.IsEligible(reserve, 0)); // first team unknown: treated as the head coach
        Assert.Empty(BackroomTeam.Plan([head], club.Address, null, writer, First, First).Abilities);

        Assert.Empty(BackroomTeam.Plan([scout], club.Address, null, writer, U21, First).Abilities); // another team
        Assert.Empty(BackroomTeam.Plan([reserve], club.Address, null, writer, First, First).Abilities);

        var plan = BackroomTeam.Plan([reserve], club.Address, null, writer, U21, First);
        Assert.Empty(plan.Skipped);
        // 102 comes from the separate Python translation of the game's function, run on these bytes after the preset's raises.
        Assert.Equal([(false, 90, 102), (true, 95, 102)], plan.Abilities.Select(change => (change.Potential, change.Before, change.After)));

        var already = reserve with { CurrentAbility = 188, PotentialAbility = 195 };
        Assert.Empty(BackroomTeam.Plan([already], club.Address, null, writer, U21, First).Abilities); // never lowered
    }

    [Fact]
    public void Backroom_puts_each_job_in_the_department_the_game_lists_it_under()
    {
        var person = Assert.Single(Repo().LoadAll());
        BackroomDepartment? Department(short job) => BackroomTeam.DepartmentOf(person with { JobCode = job });
        foreach (short job in new short[] { 2, 20, 22, 26, 34, 60, 62, 64 }) Assert.Equal(BackroomDepartment.Coaching, Department(job));
        foreach (short job in new short[] { 10, 14, 44, 58, 86, 88 }) Assert.Equal(BackroomDepartment.Recruitment, Department(job));
        foreach (short job in new short[] { 12, 38, 40, 46, 48, 50 }) Assert.Equal(BackroomDepartment.Medical, Department(job));
    }

    static StaffRecord AreaCoach(int id, int attacking, int defending, int possession, int tactical, int technical, int mental)
    {
        var attributes = new byte[Enum.GetValues<StaffAttribute>().Length];
        attributes[(int)StaffAttribute.CoachingAttacking] = (byte)attacking;
        attributes[(int)StaffAttribute.CoachingDefending] = (byte)defending;
        attributes[(int)StaffAttribute.CoachingPossession] = (byte)possession;
        attributes[(int)StaffAttribute.CoachingTactical] = (byte)tactical;
        attributes[(int)StaffAttribute.CoachingTechnical] = (byte)technical;
        attributes[(int)StaffAttribute.Determination] = attributes[(int)StaffAttribute.Authority] = attributes[(int)StaffAttribute.Motivating] = (byte)mental;
        return new StaffRecord
        {
            Address = (ulong)id, StaffAddress = (ulong)id + 1, Id = id, JobCode = 2, TeamAddress = 0x9100,
            Attributes = attributes, Personality = new byte[8], Kind = new StaffKind(0, 0, 0, null, false),
        };
    }

    [Fact]
    public void Backroom_sends_a_balanced_coach_to_the_area_the_team_lacks()
    {
        // Hand calculation (weights 6+3): coach 1 scores DefTac 180 but AttTec and PosTec 90; coach 2 scores 108 everywhere.
        // AttTec is the weakest, and coach 2 is closer to it. After that DefTec and PosTac tie lowest at 150.
        var areas = BackroomTeam.AssignCoachAreas([AreaCoach(1, 10, 20, 10, 20, 10, 18), AreaCoach(2, 12, 12, 12, 12, 12, 18)]);
        Assert.Equal(TrainingCategory.AttackingTechnical, areas[2]);
        Assert.Equal(TrainingCategory.PossessionTactical, areas[1]);
    }

    [Fact]
    public void Backroom_three_balanced_coaches_cover_defending_attacking_and_possession()
    {
        var areas = BackroomTeam.AssignCoachAreas([AreaCoach(1, 14, 14, 14, 14, 14, 14), AreaCoach(2, 14, 14, 14, 14, 14, 14), AreaCoach(3, 14, 14, 14, 14, 14, 14)]);
        Assert.Equal(TrainingCategory.DefendingTactical, areas[1]);
        Assert.Equal(TrainingCategory.AttackingTechnical, areas[2]);
        Assert.Equal(TrainingCategory.PossessionTactical, areas[3]);
    }

    [Fact]
    public void Backroom_gives_general_coaches_different_areas_and_the_rated_coach_set_pieces()
    {
        var person = Assert.Single(Repo().LoadAll());
        var writer = new StaffWriter(_w.Memory, _w.Profile, TestWorld.ModuleBase);
        var club = new ClubInfo(0x9000, "Man Utd", "Man Utd");
        var roles = new byte[15];
        Array.Fill(roles, (byte)1);
        roles[(int)StaffRole.SetPieceCoach] = 20;
        var plan = BackroomTeam.Plan([person with { Club = club, JobCode = 2, Roles = roles }], club.Address, BackroomDepartment.Coaching, writer);
        Assert.Contains(plan.Changes, change => change.Field == StaffAttribute.SetPieces || change.Field == StaffAttribute.TacticalKnowledge);
        Assert.DoesNotContain(plan.Changes, change => change.Field == StaffAttribute.CoachingAttacking || change.Field == StaffAttribute.CoachingPossession);
    }

    /// <summary>
    /// Human manager: 0x580-byte object, staff part (vtable 0x4CC086C) 0x440 below the person part (vtable 0x4CC08D8).
    /// Bytes are Duong Vu's as dumped; expected values are FMRTE 27's page for him (ID 2002081897, born 20/8/2004,
    /// reputation 8848/8852/4665, CA/PA 146/200, Hands On Approach 55 … Media Handling 72). Writing Hands On Approach 56
    /// in game changed that row only in FMRTE (Squad Discipline, also 55, stayed).
    /// </summary>
    [Fact]
    public void Reads_and_writes_a_human_manager()
    {
        const ulong HumanObj = 0x6003_0000, HumanPerson = HumanObj + 0x440;
        var o = _w.Memory.Map(HumanObj, 0x580);
        TestWorld.Set64(o, 0, TestWorld.ModuleBase + 0x4CC086C);
        Convert.FromHexString("0A0A0A0A110A0A0A0A0A0A0A0C550A0A0A0A0A0A0A0A0A0A0A0A323732324E4A323B53373C5A584C323232323232323232323246").CopyTo(o, 0x10);
        Convert.FromHexString("9022942239129200C800").CopyTo(o, 0xD4);
        Convert.FromHexString("2B37323C2D3748").CopyTo(o, 0x42D);
        TestWorld.Set64(o, 0x440, TestWorld.ModuleBase + 0x4CC08D8);
        BitConverter.GetBytes(2002081897).CopyTo(o, 0x44C);
        Convert.FromHexString("110A0A0A0A0A0A05").CopyTo(o, 0x4B0);
        Convert.FromHexString("E900D407").CopyTo(o, 0x4C8);
        const ulong Wide = 0x6000_2000;
        var v = _w.Memory.Map(Wide, 24);
        TestWorld.Set64(v, 0, TestWorld.Mainoo); TestWorld.Set64(v, 8, Person); TestWorld.Set64(v, 16, HumanPerson);
        Assert.True(_w.Memory.TryWrite(Root + 0x88, [.. BitConverter.GetBytes(Wide), .. BitConverter.GetBytes(Wide + 24)]));

        var s = Assert.Single(Repo().LoadAll(), x => x.Kind.Manager);
        Assert.Equal(2002081897, s.Id);
        Assert.Equal(new DateOnly(2004, 8, 20), s.DateOfBirth);
        Assert.Equal((8848, 8852, 4665, 146, 200), ((int)s.HomeReputation, (int)s.CurrentReputation, (int)s.WorldReputation, (int)s.CurrentAbility, (int)s.PotentialAbility));
        Assert.Equal(17, s.Attribute(StaffAttribute.CoachingAttacking));
        Assert.Equal(17, s.Attribute(StaffAttribute.Determination));
        Assert.Equal(17, s.Attribute(StaffAttribute.Authority));
        Assert.Equal(12, s.Attribute(StaffAttribute.WorkingWithYoungsters));
        Assert.Equal(new byte[] { 17, 10, 10, 10, 10, 10, 10, 5 }, s.Personality);
        Assert.Equal(new byte[] { 55, 45, 60, 50, 55, 43, 72 }, s.ManagerAttributes);
        Assert.Null(Repo().LoadAll().Single(x => !x.Kind.Manager).ManagerAttributes);

        new StaffWriter(_w.Memory, _w.Profile, TestWorld.ModuleBase).SetManagerAttribute(s, ManagerAttribute.HandsOnApproach, 56);
        Assert.Equal(Convert.FromHexString("2B37323C2D3848"), _w.Bytes(HumanObj + 0x42D, 7));
    }

    [Fact]
    public void Wrong_staff_part_vtable_is_not_staff()
    {
        Assert.True(_w.Memory.TryWrite(Obj, BitConverter.GetBytes(TestWorld.ModuleBase + 0x4CC086C)));
        Assert.Empty(Repo().LoadAll());
    }
}
