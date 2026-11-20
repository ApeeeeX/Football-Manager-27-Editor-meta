using System.Text;
using System.Xml.Linq;
using Fm27Editor.Core.History;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class LegendPlayersTests
{
    // The rows are transcribed from the supplied FMCET.CT clonePlayer calls, in XML order.
    [Theory]
    [InlineData(19230, "Cristiano Ronaldo", 18, "POR", 148, 198,
        "20,20,14,18,20,8,8,12,4,4,1,1,1,2,2,1,3,2,3,13,15,14,12,13,12,19,17,14,15,2,7,11,11,16,14,13,14,19,15,12,12,10,11,16,14,13,13,15,15,7,2,13,15,3,16,18,10,18,4,14,148,198,13,20,1,1,1,1,1,1,1,14,1,12,20,6,18,14,185,83,0x201800000308",
        20, 1, 3, 16, 14, 13, 20, 1, 18, 14, 185, 83, "0x201800000308")]
    [InlineData(19225, "Lionel Messi", 18, "ARG", 150, 200,
        "18,20,18,18,16,14,14,6,2,1,1,3,2,2,4,4,2,1,3,9,13,12,15,11,14,17,20,13,15,2,9,16,8,17,17,13,6,16,13,12,7,12,13,17,16,17,15,8,13,6,2,17,13,3,16,16,9,18,5,16,150,200,20,10,1,1,1,1,1,1,1,8,12,10,10,18,20,16,169,67,0x408c00000280440",
        16, 4, 2, 17, 16, 20, 10, 1, 20, 16, 169, 67, "0x408C00000280440")]
    [InlineData(19155, "David Beckham", 19, "ENG", 133, 178,
        "20,18,12,18,18,16,8,10,3,1,2,2,2,3,3,3,4,1,2,15,11,14,13,14,13,18,15,14,12,7,12,15,12,11,12,12,7,18,11,15,11,15,17,11,10,13,17,6,12,7,6,15,13,6,12,16,10,16,4,13,133,178,10,20,1,1,1,1,1,12,10,1,18,20,1,1,12,1,183,75,0x810880000000080",
        18, 3, 4, 11, 10, 10, 20, 12, 12, 1, 183, 75, "0x810880000000080")]
    [InlineData(19192, "Ronaldo", 19, "BRA", 156, 196,
        "16,14,14,16,10,14,12,12,2,3,4,3,1,2,1,1,2,2,1,10,14,11,16,11,13,14,19,12,17,3,8,12,10,18,16,16,9,16,16,13,13,6,11,18,17,15,12,9,12,7,2,13,13,3,17,16,9,16,4,5,156,196,14,20,1,1,1,1,1,1,1,1,1,1,1,1,1,20,183,82,0x8400800002000",
        10, 1, 2, 18, 17, 14, 20, 1, 1, 20, 183, 82, "0x8400800002000")]
    public void Imports_the_hand_checked_values_and_embeds_them(int id, string name, int age, string nation, int ca, int pa,
        string values, int professionalism, int punching, int reflexes, int dribbling, int finishing, int leftFoot,
        int rightFoot, int wingBackRight, int attackingMidRight, int striker, int height, int weight, string moves)
    {
        string group = id == 19192 ? "Strikers (ST)" : "Wide Midfielders (M/AM RL)";
        using var xml = Catalogue(group, id, $"{name} ({age} years old) - {nation} - {ca}/{pa}", values);
        foreach (var preset in new[] { Assert.Single(LegendImport.Parse(xml)), PlayerLegends.All.Single(p => p.Id == id) })
        {
            Assert.Equal((name, age, nation, ca, pa, group), (preset.Name, preset.Age, preset.Nationality, preset.CurrentAbility, preset.PotentialAbility, preset.Group));
            Assert.Equal(professionalism, preset.Personality[PersonalityAttribute.Professionalism]);
            Assert.Equal(punching, preset.Attributes[PlayerAttribute.TendencyToPunch]);
            Assert.Equal(reflexes, preset.Attributes[PlayerAttribute.Reflexes]);
            Assert.Equal(dribbling, preset.Attributes[PlayerAttribute.Dribbling]);
            Assert.Equal(finishing, preset.Attributes[PlayerAttribute.Finishing]);
            Assert.Equal(leftFoot, preset.Attributes[PlayerAttribute.LeftFoot]);
            Assert.Equal(rightFoot, preset.Attributes[PlayerAttribute.RightFoot]);
            Assert.Equal(wingBackRight, preset.Positions[PlayerPosition.WingBackRight]);
            Assert.Equal(attackingMidRight, preset.Positions[PlayerPosition.AttackingMidRight]);
            Assert.Equal(striker, preset.Positions[PlayerPosition.Striker]);
            Assert.Equal((height, weight, moves), (preset.Height, preset.Weight, preset.SourcePreferredMoves));
        }
    }

    [Fact]
    public void Includes_rows_without_a_nationality_and_rejects_a_short_script()
    {
        Assert.Equal(417, PlayerLegends.All.Count);
        Assert.Equal(6, PlayerLegends.All.Select(p => p.Group).Distinct().Count());
        Assert.Null(PlayerLegends.All.Single(p => p.Name == "Cafu").Nationality);
        using var bad = Catalogue("Strikers (ST)", 123, "A Player (19 years old) - BRA - 156/196", "1,2,3");
        Assert.Throws<InvalidDataException>(() => LegendImport.Parse(bad));
    }

    [Theory]
    [InlineData("27.1.0+2505117-steam",     0x264, 0x160, 0x177, 0x15B, 0x2FC, 0x22E, 0x310)]
    [InlineData("27.1.0+2505117-gamepass",  0x264, 0x160, 0x177, 0x15B, 0x2FC, 0x22E, 0x310)]
    [InlineData("27.1.0+2505117-mac",       0x25C, 0x158, 0x16F, 0x153, 0x2BC, 0x226, 0x2D0)]
    [InlineData("27.1.0+2505719-touch-mac", 0x1AC,  0xA8,  0xBF,  0xA3, 0x20C, 0x176, 0x220)]
    public void Applies_and_undoes_one_preset_using_the_supported_layout(string variant, int ability, int dribbling,
        int leftFoot, int attackingMidRight, int professionalism, int height, int born)
    {
        var (profile, memory, bytes, player) = Target(variant);
        TestWorld.Set16(bytes, height, 190);
        byte[] original = bytes.ToArray();
        var journal = new ChangeJournal();
        var observed = new JournalMemory(memory, journal);
        var writer = new PlayerLegendWriter(new PlayerWriter(observed, profile, TestWorld.ModuleBase));

        using (journal.Begin("Player", "Target", "Legend Players", PlayerChangeLabels.For(profile, player.Address, player.VtableRva)))
            writer.Apply(player, PlayerLegends.All.Single(p => p.Id == 19225));

        // Messi's CE slots 61–62 hold CA 150/PA 200, 44 holds Dribbling 17, and 63 holds Left Foot 20.
        Assert.Equal(new byte[] { 150, 0, 200, 0 }, bytes[ability..(ability + 4)]);
        Assert.Equal(85, bytes[dribbling]);
        Assert.Equal(100, bytes[leftFoot]);
        Assert.Equal(20, bytes[attackingMidRight]);
        Assert.Equal(16, bytes[professionalism]);
        Assert.Equal(original[height..(height + 2)], bytes[height..(height + 2)]);
        Assert.Equal(original[born..(born + 4)], bytes[born..(born + 4)]);
        var group = Assert.Single(journal.Groups);
        Assert.Equal(78, group.Entries.Count);
        Assert.Equal("17", group.Entries.Single(e => e.Name == "Dribbling").AfterText);
        var (restored, errors) = journal.Undo(group, observed);
        Assert.Equal(78, restored);
        Assert.Empty(errors);
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Leaves_Didas_unsupported_zero_rating_and_rolls_back_a_failed_batch()
    {
        var (profile, memory, bytes, player) = Target("27.1.0+2505117-mac");
        bytes[0x15A] = 42; // Heading at +0x15A in the Steam Mac player object.
        var preset = PlayerLegends.All.Single(p => p.Name == "Dida");
        Assert.Equal(0, preset.Attributes[PlayerAttribute.Heading]);
        var writer = new PlayerLegendWriter(new PlayerWriter(memory, profile, TestWorld.ModuleBase));
        byte[] before = bytes.ToArray();

        int writes = 0;
        memory.BeforeWrite = () =>
        {
            if (++writes == 3) throw new MemoryAccessException("Simulated write failure");
        };
        Assert.Throws<MemoryAccessException>(() => writer.Apply(player, preset));
        Assert.Equal(before, bytes);

        writer.Apply(player, preset);
        Assert.Equal(42, bytes[0x15A]);
        Assert.Equal(20, bytes[0x148]);  // GK position
        Assert.Equal(95, bytes[0x16C]);  // Reflexes 19 × 5
    }

    static (OffsetProfile Profile, FakeMemory Memory, byte[] Bytes, PlayerRecord Player) Target(string variant)
    {
        var profile = OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", $"{variant}.json"));
        var memory = new FakeMemory();
        var bytes = memory.Map(TestWorld.Mainoo, profile.PlayerObjectSize);
        TestWorld.Set64(bytes, 0, TestWorld.ModuleBase + profile.PlayerVtableRvas[0]);
        var player = new PlayerRepository(memory, profile, TestWorld.ModuleBase).Reload(TestWorld.Mainoo)!;
        return (profile, memory, bytes, player);
    }

    static MemoryStream Catalogue(string group, int id, string description, string values)
    {
        XElement Entry(string title, XElement content) => new("CheatEntry", new XElement("Description", $"\"{title}\""), content);
        var entry = Entry(description, new XElement("AssemblerScript", $"local table = {{ {values} }}\n\nclonePlayer(table)"));
        entry.Add(new XElement("ID", id));
        var catalogue = Entry("Legend Players >", new XElement("CheatEntries",
            Entry(group + " >", new XElement("CheatEntries", entry))));
        var table = new XElement("CheatTable", new XElement("CheatEntries", catalogue));
        return new MemoryStream(Encoding.UTF8.GetBytes(table.ToString()));
    }

    sealed class JournalMemory(FakeMemory memory, ChangeJournal journal) : IProcessMemory
    {
        public bool TryRead(ulong address, Span<byte> buffer) => memory.TryRead(address, buffer);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
        {
            var before = memory.Read(address, data.Length);
            if (!memory.TryWrite(address, data)) return false;
            journal.Record(address, before, data.ToArray());
            return true;
        }
    }
}
