using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

// Each write uses the original snapshot, so a game-side change to a pending field
// remains guarded even after another field has been saved and reloaded.
public sealed class PlayerDraft(PlayerRecord original)
{
    readonly Dictionary<FieldDescriptor<PlayerRecord>, int> changes = [];
    public PlayerRecord Original { get; } = original;
    public PlayerLegend? Legend { get; private set; }
    public int Count => changes.Count + (Legend is null ? 0 : 1);
    public int Value(FieldDescriptor<PlayerRecord> field) => changes.TryGetValue(field, out var value) ? value : DefaultValue(field);

    public void SetLegend(PlayerLegend? legend)
    {
        Legend = legend;
        if (legend is null)
            foreach (var (field, value) in changes.ToArray())
                if (field.Read(Original) == value) changes.Remove(field);
    }

    int DefaultValue(FieldDescriptor<PlayerRecord> field)
    {
        if (Legend is not { } legend) return field.Read(Original);
        if (field.Id == "CA") return legend.CurrentAbility;
        if (field.Id == "PA") return legend.PotentialAbility;
        if (Enum.TryParse<PlayerAttribute>(field.Id, out var attribute) && legend.Attributes.TryGetValue(attribute, out int value) && value > 0)
            return value;
        if (Enum.TryParse<PersonalityAttribute>(field.Id, out var personality) && legend.Personality.TryGetValue(personality, out value))
            return value;
        if (Enum.TryParse<PlayerPosition>(field.Id, out var position) && legend.Positions.TryGetValue(position, out value))
            return value;
        return field.Read(Original);
    }

    public void Stage(FieldDescriptor<PlayerRecord> field, int value)
    {
        if (field.Write is null || value < field.Min || value > field.Max) throw new ArgumentOutOfRangeException(nameof(value));
        if (value == DefaultValue(field)) changes.Remove(field); else changes[field] = value;
    }

    internal void SaveLegend(PlayerWriter writer)
    {
        var legend = Legend ?? throw new InvalidOperationException("Choose a legend first");
        var overrides = changes.Keys.Select(field => field.Id).ToHashSet(StringComparer.Ordinal);
        new PlayerLegendWriter(writer).Apply(Original, legend, staged =>
        {
            foreach (var (field, value) in changes)
                if (value != field.Read(Original)) field.Write!(staged, Original, value);
        }, overrides);
        Legend = null;
        changes.Clear();
    }

    public PlayerRecord Save(IEditService edits)
    {
        var latest = Original;
        foreach (var (field, value) in changes.ToArray())
        {
            latest = edits.Apply(Original, field, value);
            changes.Remove(field);
        }
        return latest;
    }
    public PlayerDraft Rebase(PlayerRecord fresh)
    {
        var rebased = new PlayerDraft(fresh);
        rebased.SetLegend(Legend);
        foreach (var (field, value) in changes) rebased.Stage(field, value);
        return rebased;
    }
}
