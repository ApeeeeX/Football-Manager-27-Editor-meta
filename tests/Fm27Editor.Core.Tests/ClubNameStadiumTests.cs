using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// "Can Name Stadium After Person" on a club's liked people. Rows are the 16-byte records read from Steam 27.1.0 RAM
/// (22/11/2026); the IGE ticks the box for exactly Alex Ferguson at Man Utd and Pep Guardiola, Vincent Kompany and
/// David Silva at Man City. Giggs and Mancini also have value 100 and Permanent, but no tick.
/// </summary>
public class ClubNameStadiumTests
{
    const ulong Club = 0x5000_0000, Records = 0x5100_0000;

    static readonly (string Row, bool Named)[] ManCity =
    [
        ("80A798577402000002000301644F01FF", true),   // Pep Guardiola
        ("4061A05774020000010003012D0000FF", false),  // Yaya Touré
        ("40CBB7577402000001000301644F01FF", true),   // Vincent Kompany
        ("C0949B577402000002000301644F00FF", false),  // Roberto Mancini
        ("88D6A65A7402000001000301644F01FF", true),   // David Silva
    ];

    static (FakeMemory Memory, OffsetProfile Profile) World(params string[] rows)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", "27.1.0+2505117-steam.json"));
        var o = profile.Club!.Relationships!;
        var memory = new FakeMemory();
        var club = memory.Map(Club, o.Vector + 24);
        TestWorld.Set64(club, 0, TestWorld.ModuleBase + profile.Club.ClubVtableRva);
        TestWorld.Set64(club, o.Vector, Records);
        TestWorld.Set64(club, o.Vector + 8, Records + (ulong)(rows.Length * o.RecordSize));
        TestWorld.Set64(club, o.Vector + 16, Records + (ulong)(rows.Length * o.RecordSize));
        var records = memory.Map(Records, rows.Length * o.RecordSize);
        for (int i = 0; i < rows.Length; i++) Convert.FromHexString(rows[i]).CopyTo(records, i * o.RecordSize);
        return (memory, profile);
    }

    [Fact]
    public void Reads_the_tick_the_ige_shows_for_each_liked_person()
    {
        var (memory, profile) = World(ManCity.Select(r => r.Row).ToArray());
        var rows = new ClubRelationshipReader(memory, profile, TestWorld.ModuleBase).Read(Club);
        Assert.All(rows, row => Assert.Equal(ClubRelationshipKind.LikesPerson, row.Kind));
        Assert.Equal(ManCity.Select(r => (bool?)r.Named), rows.Select(r => r.CanNameStadium));
    }

    [Fact]
    public void Ticking_writes_only_that_byte_and_rivals_cannot_be_ticked()
    {
        const string mancini = "C0949B577402000002000301644F00FF";
        const string liverpool = "00000000000000000100010564 4F02FF";
        var (memory, profile) = World(mancini, liverpool.Replace(" ", ""));
        var reader = new ClubRelationshipReader(memory, profile, TestWorld.ModuleBase);
        var writer = new ClubRelationshipWriter(memory, profile, TestWorld.ModuleBase);
        var rows = reader.Read(Club);
        Assert.Equal(ClubRelationshipKind.Rival, rows[1].Kind);
        Assert.Null(rows[1].CanNameStadium);
        Assert.Throws<ArgumentException>(() => writer.SetDetails(Club, rows[1], null, null, null, canNameStadium: true));

        writer.SetDetails(Club, rows[0], null, null, null, canNameStadium: true);
        Assert.Equal("C0949B577402000002000301644F01FF", Convert.ToHexString(memory.Read(Records, 16)));
        Assert.True(reader.Read(Club)[0].CanNameStadium);
    }
}
