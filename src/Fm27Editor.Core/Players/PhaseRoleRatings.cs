using System.Text.Json;

namespace Fm27Editor.Core.Players;

public enum PossessionPhase { InPossession, OutOfPossession }
public sealed record PhaseRoleAttribute(PlayerAttribute Attribute, bool Key, bool LowerIsBetter);
public sealed record PhaseRole(string Id, string PositionGroup, PossessionPhase Phase, IReadOnlyList<PhaseRoleAttribute> Attributes);

/// <summary>Reads a user-supplied modern role catalog. Never substitutes the legacy role/duty list.</summary>
public sealed class PhaseRoleRatings
{
    public IReadOnlyList<PhaseRole> Roles { get; }
    readonly double keyWeight, preferableWeight, avoidWeight;
    readonly double[] penalties;
    public string Version { get; }
    PhaseRoleRatings(List<PhaseRole> roles, double key, double preferable, double avoid, double[] penalties, string version)
    { Roles = roles.AsReadOnly(); keyWeight = key; preferableWeight = preferable; avoidWeight = avoid; this.penalties = penalties; Version = version; }

    public static PhaseRoleRatings Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Loads a catalog from an embedded resource or any other stream; the caller owns the stream.</summary>
    public static PhaseRoleRatings Load(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        double Weight(string name)
        {
            double value = root.GetProperty(name).GetDouble();
            if (!double.IsFinite(value) || value < 0 || value > 1000) throw new FormatException("Invalid role group weight");
            return value;
        }
        var penalties = root.GetProperty("PositionRatingPenalties").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (penalties.Length != 20 || penalties.Any(x => !double.IsFinite(x) || x < 0 || x > 100)) throw new FormatException("Invalid position penalties");
        var roles = new List<PhaseRole>();
        foreach (var group in root.GetProperty("GeneralPositionToRoleAndDutiesDefinitionsMap").EnumerateObject())
        {
            if (!Groups.Contains(group.Name)) throw new FormatException("Unknown role position group: " + group.Name);
            foreach (var row in group.Value.EnumerateArray())
            foreach (var phase in row.GetProperty("DutiesAttributes").EnumerateObject())
            {
                if (!Enum.TryParse<PossessionPhase>(phase.Name, out var phaseId) || !Enum.IsDefined(phaseId)) throw new FormatException("Unknown role phase");
                var attrs = phase.Value.EnumerateArray().Select(a => new PhaseRoleAttribute(ParseAttribute(a.GetProperty("Name").GetString()!),
                    a.GetProperty("KeyAttribute").GetBoolean(), a.TryGetProperty("Avoid", out var avoid) && avoid.GetBoolean())).ToArray();
                if (attrs.Length == 0 || attrs.Select(a => a.Attribute).Distinct().Count() != attrs.Length) throw new FormatException("Invalid role attributes");
                string id = row.GetProperty("Role").GetString() ?? throw new FormatException("Missing role name");
                if (roles.Any(r => r.Id == id && r.PositionGroup == group.Name && r.Phase == phaseId)) throw new FormatException("Duplicate role");
                roles.Add(new(id, group.Name, phaseId, Array.AsReadOnly(attrs)));
            }
        }
        if (roles.Count == 0) throw new FormatException("Empty role catalog");
        return new(roles, Weight("KeyAttributesWeight"), Weight("PreferableAttributesWeight"), Weight("AvoidAttributesWeight"), penalties,
            root.GetProperty("SettingsVersion").GetString() ?? "Unknown");
    }

    static PlayerAttribute ParseAttribute(string name)
    {
        string mapped = name switch { "AerialAbility" => "AerialReach", "Workrate" => "WorkRate", "Jumping" => "JumpingReach", "Freekicks" => "FreeKicks", "Longthrows" => "LongThrows", _ => name };
        return Enum.TryParse<PlayerAttribute>(mapped, out var a) && Enum.IsDefined(a) ? a : throw new FormatException("Unknown role attribute: " + name);
    }
    static readonly string[] Groups = ["Goalkeeper", "DefenderCentral", "FullBack", "WingBack", "DefensiveMidfielder", "MidfielderCentral", "WideMidfielder", "Winger", "AttackingMidfielderCentral", "Striker"];
    public static string Group(PlayerPosition p) => p switch
    {
        PlayerPosition.DefenderLeft or PlayerPosition.DefenderRight => "FullBack",
        PlayerPosition.WingBackLeft or PlayerPosition.WingBackRight => "WingBack",
        PlayerPosition.MidfielderLeft or PlayerPosition.MidfielderRight => "WideMidfielder",
        PlayerPosition.AttackingMidLeft or PlayerPosition.AttackingMidRight => "Winger",
        PlayerPosition.AttackingMidCentral => "AttackingMidfielderCentral",
        _ => p.ToString(),
    };
    public IReadOnlyList<PhaseRole> At(PlayerPosition position, PossessionPhase phase) => Roles.Where(r => r.PositionGroup == Group(position) && r.Phase == phase).ToArray();

    public double? Score(PlayerRecord player, PlayerPosition position, PhaseRole role) =>
        Score(player.RawAttributes, player.AttributeScale, position, (int)position < player.Positions.Length ? player.Position(position) : 0, role);

    public double? Score(ReadOnlySpan<byte> attributes, int attributeScale, PlayerPosition position, int familiarity, PhaseRole role)
    {
        if (role.PositionGroup != Group(position) || !Roles.Contains(role)) throw new ArgumentException("Role does not belong to this position/catalog");
        if (attributeScale <= 0 || familiarity is < 1 or > 20) return null;
        double sum = 0, totalWeight = 0;
        foreach (int group in new[] { 0, 1, 2 })
        {
            var attrs = role.Attributes.Where(a => (a.LowerIsBetter ? 2 : a.Key ? 0 : 1) == group).ToArray();
            double weight = group == 0 ? keyWeight : group == 1 ? preferableWeight : avoidWeight;
            if (attrs.Length == 0 || weight == 0) continue;
            double values = 0;
            foreach (var attr in attrs)
            {
                if ((int)attr.Attribute >= attributes.Length || attributes[(int)attr.Attribute] == 0) return null;
                int value = AttributeScaling.ToDisplay(attributes[(int)attr.Attribute], attributeScale);
                if (value is < 1 or > 20) return null;
                values += group == 2 ? 20 - value : value;
            }
            sum += values / attrs.Length * weight;
            totalWeight += weight;
        }
        return totalWeight == 0 ? null : Math.Clamp(sum / totalWeight * 5 * (1 - penalties[familiarity - 1] / 100), 0, 100);
    }
}
