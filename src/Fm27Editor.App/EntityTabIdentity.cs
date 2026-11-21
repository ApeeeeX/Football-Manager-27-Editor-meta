using Avalonia.Controls;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

internal readonly record struct EntityTabIdentity(int? Id, string? Name = null, DateOnly? BirthDate = null, bool Generated = false)
{
    public static EntityTabIdentity Database(int? id) => new(id is > 0 ? id : null);

    public static EntityTabIdentity Person(int? id, string name, DateOnly? birthDate) =>
        id is > 0 ? new(id, name, birthDate, RegenIds.Current is { } first && id >= first) : default;

    public bool Matches(EntityTabIdentity other) => Id is not null && Id == other.Id &&
        (!(Generated || other.Generated) || !string.IsNullOrEmpty(Name) && Name == other.Name && BirthDate == other.BirthDate);

    // Repeated catalogue rows for one address are harmless. Two objects with the same key are ambiguous.
    public T? Find<T>(IEnumerable<T> records, Func<T, EntityTabIdentity> key, Func<T, ulong> address) where T : class
    {
        if (Id is null) return null;
        T? match = null;
        foreach (var record in records)
        {
            if (!Matches(key(record))) continue;
            if (match is not null && address(match) != address(record)) return null;
            match = record;
        }
        return match;
    }
}

internal sealed record EntityTabContent(ulong Address, string Title, Control Body, Action Restore);
