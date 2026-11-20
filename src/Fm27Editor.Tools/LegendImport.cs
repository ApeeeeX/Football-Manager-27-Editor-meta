using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Fm27Editor.Core.Players;

internal static partial class LegendImport
{
    // One-based Lua slots 9–60, in clonePlayer's order (not the order of the FM27 enums).
    static readonly PlayerAttribute[] Attributes =
    [
        PlayerAttribute.AerialReach, PlayerAttribute.CommandOfArea, PlayerAttribute.Communication,
        PlayerAttribute.Eccentricity, PlayerAttribute.Handling, PlayerAttribute.Kicking,
        PlayerAttribute.OneOnOnes, PlayerAttribute.TendencyToPunch, PlayerAttribute.Reflexes,
        PlayerAttribute.RushingOut, PlayerAttribute.Throwing,
        PlayerAttribute.Aggression, PlayerAttribute.Anticipation, PlayerAttribute.Bravery,
        PlayerAttribute.Composure, PlayerAttribute.Concentration, PlayerAttribute.Decisions,
        PlayerAttribute.Determination, PlayerAttribute.Flair, PlayerAttribute.Leadership,
        PlayerAttribute.OffTheBall, PlayerAttribute.Positioning, PlayerAttribute.Teamwork,
        PlayerAttribute.Vision, PlayerAttribute.WorkRate,
        PlayerAttribute.Acceleration, PlayerAttribute.Agility, PlayerAttribute.Balance,
        PlayerAttribute.JumpingReach, PlayerAttribute.NaturalFitness, PlayerAttribute.Pace,
        PlayerAttribute.Stamina, PlayerAttribute.Strength,
        PlayerAttribute.Corners, PlayerAttribute.Crossing, PlayerAttribute.Dribbling,
        PlayerAttribute.Finishing, PlayerAttribute.FirstTouch, PlayerAttribute.FreeKicks,
        PlayerAttribute.Heading, PlayerAttribute.LongShots, PlayerAttribute.LongThrows,
        PlayerAttribute.Marking, PlayerAttribute.Passing, PlayerAttribute.PenaltyTaking,
        PlayerAttribute.Tackling, PlayerAttribute.Technique,
        PlayerAttribute.Consistency, PlayerAttribute.Dirtiness, PlayerAttribute.ImportantMatches,
        PlayerAttribute.InjuryProneness, PlayerAttribute.Versatility,
    ];

    static readonly PersonalityAttribute[] Personality =
    [
        PersonalityAttribute.Adaptability, PersonalityAttribute.Ambition, PersonalityAttribute.Loyalty,
        PersonalityAttribute.Pressure, PersonalityAttribute.Professionalism, PersonalityAttribute.Sportsmanship,
        PersonalityAttribute.Temperament, PersonalityAttribute.Controversy,
    ];

    static readonly PlayerPosition[] Positions =
    [
        PlayerPosition.Goalkeeper, PlayerPosition.DefenderLeft, PlayerPosition.DefenderCentral,
        PlayerPosition.DefenderRight, PlayerPosition.WingBackLeft, PlayerPosition.WingBackRight,
        PlayerPosition.DefensiveMidfielder, PlayerPosition.MidfielderLeft, PlayerPosition.MidfielderCentral,
        PlayerPosition.MidfielderRight, PlayerPosition.AttackingMidLeft, PlayerPosition.AttackingMidCentral,
        PlayerPosition.AttackingMidRight, PlayerPosition.Striker,
    ];

    [GeneratedRegex(@"^(.+) \((\d+) years old\)(?: - ([A-Z]{3}))? - (\d+)/(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DescriptionPattern();

    [GeneratedRegex(@"\blocal\s+table\s*=\s*\{([^{}]+)\}\s*clonePlayer\(table\)", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptPattern();

    internal static int Run(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: import-legends <FMCET.CT> <out.json>");
            return 2;
        }
        using var input = File.OpenRead(args[0]);
        var presets = Parse(input);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };
        using var output = new StreamWriter(args[1], append: false, new UTF8Encoding(false)) { NewLine = "\n" };
        output.WriteLine("[");
        for (int i = 0; i < presets.Count; i++)
        {
            output.Write("  ");
            output.Write(JsonSerializer.Serialize(presets[i], options));
            output.WriteLine(i + 1 == presets.Count ? "" : ",");
        }
        output.WriteLine("]");
        Console.WriteLine($"Imported {presets.Count} Legend Players presets.");
        return 0;
    }

    internal static IReadOnlyList<PlayerLegend> Parse(Stream source)
    {
        using var xml = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        var table = XDocument.Load(xml);
        var catalogue = table.Descendants("CheatEntry").SingleOrDefault(e => EntryTitle(e) == "Legend Players")
            ?? throw new InvalidDataException("Legend Players group was not found in the table");
        var presets = Children(catalogue)
            .SelectMany(group => Children(group).Select(entry => ParseEntry(EntryTitle(group), entry)))
            .ToArray();
        if (presets.Length == 0 || presets.Select(p => p.Id).Distinct().Count() != presets.Length)
            throw new InvalidDataException("Legend Players catalogue is empty or contains duplicate IDs");
        return presets;
    }

    static IEnumerable<XElement> Children(XElement entry) => entry.Elements("CheatEntries").Elements("CheatEntry");

    static string EntryTitle(XElement entry) => ((string?)entry.Element("Description") ?? "").Trim('"').TrimEnd('>', ' ');

    static PlayerLegend ParseEntry(string group, XElement entry)
    {
        string title = EntryTitle(entry);
        Match description = DescriptionPattern().Match(title);
        Match script = ScriptPattern().Match((string?)entry.Element("AssemblerScript") ?? "");
        if (group.Length == 0 || !description.Success || !script.Success)
            throw new InvalidDataException($"Invalid Legend Players entry: {title}");

        string[] values = script.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length != 81)
            throw new InvalidDataException($"Expected 81 preset values for {title}, got {values.Length}");
        int[] ratings = values.Take(80).Select(Number).ToArray();
        int ca = ratings[60], pa = ratings[61];
        if (ca != Number(description.Groups[4].Value) || pa != Number(description.Groups[5].Value)
            || ca is < 1 or > 200 || pa is < 1 or > 200)
            throw new InvalidDataException($"Ability values do not match the description for {title}");

        var attributes = Ratings(Attributes, ratings, 8, 0);
        attributes.Add(PlayerAttribute.LeftFoot, Rating(ratings[62], 1));
        attributes.Add(PlayerAttribute.RightFoot, Rating(ratings[63], 1));
        string moves = values[80];
        if (!moves.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(moves.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong bits))
            throw new InvalidDataException($"Invalid source preferred-move mask for {title}");

        return new PlayerLegend(Number((string?)entry.Element("ID") ?? ""), description.Groups[1].Value, group,
            Number(description.Groups[2].Value), description.Groups[3].Success ? description.Groups[3].Value : null,
            ca, pa, attributes, Ratings(Personality, ratings, 0, 1), Ratings(Positions, ratings, 64, 1),
            ratings[78], ratings[79], $"0x{bits:X}");
    }

    static Dictionary<T, int> Ratings<T>(T[] fields, int[] values, int start, int min) where T : struct, Enum =>
        fields.Select((field, index) => (field, value: Rating(values[start + index], min))).ToDictionary(x => x.field, x => x.value);

    static int Rating(int value, int min) => value >= min && value <= 20
        ? value : throw new InvalidDataException($"Preset rating {value} is outside {min}–20");

    static int Number(string value) => int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
}
