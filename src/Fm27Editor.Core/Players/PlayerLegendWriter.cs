namespace Fm27Editor.Core.Players;

public sealed class PlayerLegendWriter(PlayerWriter writer)
{
    /// <summary>Applies the preset and any explicitly staged player edits as one guarded write batch.</summary>
    public void Apply(PlayerRecord player, PlayerLegend legend, Action<PlayerWriter>? additionalEdits = null,
        IReadOnlySet<string>? overriddenFields = null) => writer.ApplyBatch(staged =>
    {
        bool Included(string field) => overriddenFields?.Contains(field) != true;

        if (Included("CA") && player.CurrentAbility != legend.CurrentAbility)
            staged.SetCurrentAbility(player, legend.CurrentAbility);
        if (Included("PA") && player.PotentialAbility != legend.PotentialAbility)
            staged.SetPotentialAbility(player, legend.PotentialAbility);

        foreach (var (field, value) in legend.Attributes)
            // The CE catalogue contains a zero Heading rating for Dida. FM27's editable range starts at 1.
            if (value != 0 && Included(field.ToString()) && player.Raw(field) != value * player.AttributeScale)
                staged.SetAttribute(player, field, value);
        foreach (var (field, value) in legend.Personality)
            if (Included(field.ToString()) && player.PersonalityValue(field) != value)
                staged.SetPersonality(player, field, value);
        foreach (var (field, value) in legend.Positions)
            if (Included(field.ToString()) && player.Position(field) != value)
                staged.SetPosition(player, field, value);

        additionalEdits?.Invoke(staged);
    });
}
