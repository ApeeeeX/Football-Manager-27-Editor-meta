using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fm27Editor.Core.Players;

/// <summary>A historical preset. The display attributes use the same 1–20 scale as the player editor.</summary>
public sealed record PlayerLegend(
    int Id, string Name, string Group, int Age, string? Nationality,
    int CurrentAbility, int PotentialAbility,
    IReadOnlyDictionary<PlayerAttribute, int> Attributes,
    IReadOnlyDictionary<PersonalityAttribute, int> Personality,
    IReadOnlyDictionary<PlayerPosition, int> Positions,
    int Height, int Weight, string SourcePreferredMoves);

public static class PlayerLegends
{
    static readonly Lazy<IReadOnlyList<PlayerLegend>> Presets = new(() =>
    {
        using var source = typeof(PlayerLegends).Assembly.GetManifestResourceStream("Fm27Editor.Players.Legends.json")
            ?? throw new InvalidOperationException("Legend Players catalogue is missing");
        return Read(source);
    });

    public static IReadOnlyList<PlayerLegend> All => Presets.Value;

    public static IReadOnlyList<PlayerLegend> Read(Stream source) =>
        JsonSerializer.Deserialize(source, PlayerLegendsJson.Default.PlayerLegendArray) is { } presets
            ? Array.AsReadOnly(presets)
            : throw new InvalidDataException("Invalid Legend Players catalogue");
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PlayerLegend[]))]
internal partial class PlayerLegendsJson : JsonSerializerContext;
