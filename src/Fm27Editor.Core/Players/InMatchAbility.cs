using System.Buffers.Binary;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

public sealed record InMatchMember(PlayerRecord Player, ClubInfo Side);

public sealed class InMatchFixture
{
    internal InMatchFixture(InMatchCopy[] copies, InMatchMember[] members, ClubInfo[] teams)
    {
        Copies = copies;
        Members = Array.AsReadOnly(members);
        Teams = Array.AsReadOnly(teams);
    }

    internal InMatchCopy[] Copies { get; }
    public IReadOnlyList<InMatchMember> Members { get; }
    public IReadOnlyList<ClubInfo> Teams { get; }
    public string Name => string.Join(" – ", Teams.Select(team => team.DisplayName));

    /// <summary>A new scan may find extra copies. At least one validated object must still belong to the previous selection.</summary>
    public bool IsSameFixture(InMatchFixture other) =>
        Teams.Select(team => team.Address).SequenceEqual(other.Teams.Select(team => team.Address)) &&
        Members.Select(member => member.Player.Id).Order().SequenceEqual(other.Members.Select(member => member.Player.Id).Order()) &&
        Copies.Select(copy => (copy.Address, copy.Fixture)).Intersect(other.Copies.Select(copy => (copy.Address, copy.Fixture))).Any();
}

internal sealed record InMatchCopy(ulong Address, ulong Source, ulong Fixture, ulong Side, ulong PersonVtable, PlayerRecord Player);

public sealed record InMatchChanges(bool MaxPerformance, IReadOnlyDictionary<PlayerAttribute, int> Attributes, int? ConditionPercent, int? Morale = null);
public sealed record InMatchPlayerValues(IReadOnlyDictionary<PlayerAttribute, int> Attributes, int? ConditionPercent, int? Morale);
public readonly record struct InMatchResult(int PlayersChanged, int CopiesChanged);

/// <summary>Edits the simulation's temporary player data through the verified profile. No edit targets a permanent player object.</summary>
public sealed class InMatchAbility(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    readonly InMatchOffsets offsets = profile.InMatch ?? throw new NotSupportedException("In-match editing is unavailable on this game build");
    readonly int firstAttribute = profile.Attributes.Values.Min();
    readonly int firstPosition = profile.Positions.Values.Min();

    public bool CanSetMorale => offsets.Morale is not null;
    public IEnumerable<PlayerAttribute> Attributes => profile.Attributes.Keys;

    public IReadOnlyDictionary<int, InMatchPlayerValues> ReadPlayers(InMatchFixture fixture, CancellationToken ct = default)
    {
        var values = new Dictionary<int, InMatchPlayerValues>();
        byte[] attributes = new byte[profile.Attributes.Values.Max() - firstAttribute + 1];
        byte[] condition = new byte[2], live = new byte[4], morale = new byte[1];
        var replicas = fixture.Copies.GroupBy(c => c.Fixture).Select(g => g.ToArray()).ToArray();
        var active = SelectActiveReplica(replicas, condition, live, ct);
        foreach (var copy in active is null ? fixture.Copies : active.Concat(replicas.Where(r => !ReferenceEquals(r, active)).SelectMany(r => r)))
        {
            ct.ThrowIfCancellationRequested();
            int uid = copy.Player.Id!.Value;
            if (values.ContainsKey(uid)) continue;
            try
            {
                ValidateCopy(memory, copy, uid);
                if (!memory.TryRead(At(copy, offsets.AttributeBase), attributes)) continue;
                int? fitness = ReadConditionPercent(copy, condition, live);
                int? spirit = offsets.Morale is { } at && memory.TryRead(At(copy, at), morale) ? (sbyte)morale[0] : null;
                values.Add(uid, new(profile.Attributes.ToDictionary(pair => pair.Key,
                    pair => AttributeScaling.ToDisplay(attributes[pair.Value - firstAttribute], profile.AttributeScale)), fitness, spirit));
            }
            catch (MemoryAccessException) { }
            catch (StaleObjectException) { }
        }
        return values;
    }

    InMatchCopy[]? SelectActiveReplica(InMatchCopy[][] replicas, byte[] condBuf, byte[] liveBuf, CancellationToken ct)
    {
        InMatchCopy[]? firstTier1 = null;
        foreach (var replica in replicas)
        {
            int tier2 = 0, tier1 = 0;
            foreach (var copy in replica)
            {
                ct.ThrowIfCancellationRequested();
                try { ValidateCopy(memory, copy, copy.Player.Id!.Value); }
                catch (MemoryAccessException) { continue; }
                catch (StaleObjectException) { continue; }
                if (!memory.TryRead(At(copy, offsets.Condition), condBuf)) continue;
                ushort condU16 = BinaryPrimitives.ReadUInt16LittleEndian(condBuf);
                if (condU16 == 0) continue;
                tier1++;
                ushort initialU16 = memory.TryRead(At(copy, offsets.InitialCondition), condBuf)
                    ? BinaryPrimitives.ReadUInt16LittleEndian(condBuf) : (ushort)0;
                if (initialU16 == 0) continue;
                if (offsets.LiveCondition is { } liveOff)
                {
                    if (memory.TryRead(At(copy, liveOff), liveBuf) &&
                        BinaryPrimitives.ReadUInt32LittleEndian(liveBuf) is > 0 and var liveU32 &&
                        liveU32 < (uint)initialU16 * 100)
                        tier2++;
                }
                else if (condU16 < initialU16)
                    tier2++;
            }
            if (tier2 > 0) return replica;
            firstTier1 ??= tier1 > 0 ? replica : null;
        }
        return firstTier1 ?? replicas.FirstOrDefault();
    }

    int? ReadConditionPercent(InMatchCopy copy, byte[] condBuf, byte[] liveBuf)
    {
        if (memory.TryRead(At(copy, offsets.Condition), condBuf) &&
            BinaryPrimitives.ReadUInt16LittleEndian(condBuf) is > 0 and var condU16)
        {
            if (offsets.LiveCondition is { } liveOff && memory.TryRead(At(copy, liveOff), liveBuf) &&
                BinaryPrimitives.ReadUInt32LittleEndian(liveBuf) is > 0 and var liveU32)
                return Math.Clamp((int)Math.Round(liveU32 / 10_000.0), 0, 100);
            return condU16 / 100;
        }
        if (memory.TryRead(At(copy, offsets.InitialCondition), condBuf) &&
            BinaryPrimitives.ReadUInt16LittleEndian(condBuf) is > 0 and var initU16)
            return initU16 / 100;
        return null;
    }

    public IReadOnlyList<InMatchFixture> FindFixtures(IReadOnlyList<PlayerRecord> players, CancellationToken ct = default)
    {
        if (profile.Fields.Id is not { } idOffset) return [];
        var people = players.Where(p => p.Id is > 0).ToLookup(p => p.Id!.Value);
        var clubs = players.Select(p => p.Club).OfType<ClubInfo>().Where(c => c.DisplayName.Length > 0)
            .GroupBy(c => c.Address).ToDictionary(g => g.Key, g => g.First());
        int headerSize = Math.Max(offsets.Data, Math.Max(offsets.SourcePerson, Math.Max(offsets.Fixture, offsets.Side))) + sizeof(ulong);
        byte[] header = new byte[headerSize];
        byte[] person = new byte[offsets.PersonUid + sizeof(int)];
        var copies = new List<InMatchCopy>();
        foreach (var hit in HeapVtableScanner.ScanReadableSmallFirst(memory, moduleBase + offsets.PlayerVtable, 4UL * 1024 * 1024, 16UL * 1024 * 1024, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (!memory.TryRead(hit.Address, header) || Word(header, 0) != hit.Vtable ||
                Word(header, offsets.Data) != moduleBase + offsets.DataVtable) continue;
            ulong source = Word(header, offsets.SourcePerson), fixture = Word(header, offsets.Fixture);
            if (source == 0 || fixture == 0 || !memory.TryRead(source, person)) continue;
            ulong personVtable = Word(person, 0);
            if (personVtable != moduleBase + offsets.PersonVtable && personVtable != moduleBase + offsets.PlayerStaffPersonVtable) continue;
            int uid = BinaryPrimitives.ReadInt32LittleEndian(person.AsSpan(offsets.PersonUid));
            var player = people[uid].FirstOrDefault(p =>
                profile.PlayerField(p, idOffset) == source + (ulong)offsets.PersonUid &&
                personVtable == moduleBase + (profile.PlayerInsertions.ContainsKey(p.VtableRva) ? offsets.PlayerStaffPersonVtable : offsets.PersonVtable));
            if (player is not null)
                copies.Add(new(hit.Address, source, fixture, Word(header, offsets.Side), personVtable, player));
        }

        var playingClub = copies.Select(c => c.Player).DistinctBy(p => p.Id).ToDictionary(p => p.Id!.Value,
            p => PlayingClub.Of(memory, profile, moduleBase, p));
        var rosters = copies.GroupBy(c => c.Fixture)
            .Where(g => g.Count() == g.Select(c => c.Player.Id).Distinct().Count())
            .GroupBy(g => string.Join(",", g.Select(c => c.Player.Id).Order()));
        var fixtures = new List<InMatchFixture>();
        foreach (var roster in rosters)
        {
            var sample = roster.First().ToArray();
            var sides = sample.GroupBy(c => playingClub[c.Player.Id!.Value]).Where(g => clubs.ContainsKey(g.Key) && g.Count() >= 7).ToArray();
            if (sides.Length != 2) continue;
            var teams = sides.Select(g => clubs[g.Key]).OrderBy(c => c.Address).ToArray();
            var first = sides.Single(g => g.Key == teams[0].Address).Select(c => c.Player.Id!.Value).ToHashSet();
            var second = sides.Single(g => g.Key == teams[1].Address).Select(c => c.Player.Id!.Value).ToHashSet();
            var sideEvidence = FindSide(roster, first, second);
            if (sideEvidence is null && first.Count + second.Count != sample.Length) continue;
            first = sideEvidence ?? first;
            var members = sample.Select(c => new InMatchMember(c.Player, teams[first.Contains(c.Player.Id!.Value) ? 0 : 1]))
                .OrderBy(member => member.Player.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            fixtures.Add(new(roster.SelectMany(g => g).ToArray(), members, teams));
        }
        return fixtures.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    // One match can have multiple simulation copies. Some store one side pointer for everyone. A split copy can
    // establish the side of players whose permanent club differs, provided both sets of club players agree.
    static HashSet<int>? FindSide(IEnumerable<IEnumerable<InMatchCopy>> replicas, HashSet<int> first, HashSet<int> second)
    {
        HashSet<int>? verified = null;
        foreach (var replica in replicas)
        {
            var groups = replica.GroupBy(c => c.Side).ToArray();
            if (groups.Length != 2 || groups.Any(g => g.Key == 0)) continue;
            var ids = groups.Select(g => g.Select(c => c.Player.Id!.Value).ToHashSet()).ToArray();
            var match = ids.FirstOrDefault(side => first.IsSubsetOf(side) && !second.Overlaps(side));
            if (match is null) continue;
            if (verified is not null && !verified.SetEquals(match)) return null;
            verified = match;
        }
        return verified;
    }

    public InMatchResult Apply(InMatchFixture fixture, IReadOnlySet<int> playerIds, InMatchChanges changes,
        PlayerPosition? position = null, int minimumFamiliarity = 18)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (playerIds.Count == 0) return default;
        if (changes.ConditionPercent is < 50 or > 100) throw new ArgumentOutOfRangeException(nameof(changes), "Condition must be 50–100%");
        if (changes.Morale is < 1 or > 20 || changes.Morale.HasValue && !CanSetMorale)
            throw new ArgumentOutOfRangeException(nameof(changes), "Morale must be supported and between 1 and 20");
        if (minimumFamiliarity is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(minimumFamiliarity));
        var ratings = changes.Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        if (ratings.Any(pair => !profile.Attributes.ContainsKey(pair.Key) || pair.Value is < 1 or > 20))
            throw new ArgumentOutOfRangeException(nameof(changes), "Attributes must be mapped and between 1 and 20");

        if (changes.MaxPerformance)
            foreach (var field in profile.Attributes.Keys.Where(field => PlayerAttributeInfo.GroupOf(field) != AttributeGroup.Hidden))
                ratings.TryAdd(field, 20);
        var fields = ratings.Select(pair => (Offset: offsets.AttributeBase + profile.Attributes[pair.Key] - firstAttribute,
            Raw: AttributeScaling.ToRaw(pair.Value, profile.AttributeScale), OnlyRaise: !changes.Attributes.ContainsKey(pair.Key)))
            .OrderBy(field => field.Offset).ToArray();
        var runs = new List<(int Offset, (int Offset, byte Raw, bool OnlyRaise)[] Fields)>();
        for (int start = 0; start < fields.Length;)
        {
            int end = start + 1;
            while (end < fields.Length && fields[end].Offset == fields[end - 1].Offset + 1) end++;
            runs.Add((fields[start].Offset, fields[start..end]));
            start = end;
        }
        byte[]? conditionBytes = changes.ConditionPercent is { } condition ? BitConverter.GetBytes((ushort)(condition * 100)) : null;
        byte[]? liveConditionBytes = changes.ConditionPercent is { } livePct && offsets.LiveCondition is not null
            ? BitConverter.GetBytes((uint)(livePct * 10_000)) : null;
        int? positionOffset = position is { } pos ? offsets.PositionBase + profile.Positions[pos] - firstPosition : null;
        var touched = new HashSet<int>();
        int copies = 0;
        MemoryWriteBatch.Run(memory, staged =>
        {
            foreach (var copy in fixture.Copies.Where(c => playerIds.Contains(c.Player.Id!.Value)))
            {
                int uid = copy.Player.Id!.Value;
                ValidateCopy(staged, copy, uid);
                if (positionOffset is { } at && staged.Read(At(copy, at), 1)[0] < minimumFamiliarity) continue;

                bool changed = false;
                foreach (var (offset, run) in runs)
                {
                    ulong address = At(copy, offset);
                    var before = staged.Read(address, run.Length);
                    var next = new byte[run.Length];
                    for (int i = 0; i < run.Length; i++)
                        next[i] = run[i].OnlyRaise ? Math.Max(before[i], run[i].Raw) : run[i].Raw;
                    changed |= Stage(staged, address, before, next);
                }
                if (conditionBytes is not null)
                {
                    ulong initial = At(copy, offsets.InitialCondition);
                    ulong effective = At(copy, offsets.Condition);
                    changed |= Stage(staged, initial, staged.Read(initial, conditionBytes.Length), conditionBytes);
                    byte[] condBefore = staged.Read(effective, conditionBytes.Length);
                    if (offsets.LiveCondition is not { } liveOff)
                    {
                        changed |= Stage(staged, effective, condBefore, conditionBytes);
                    }
                    else if (BinaryPrimitives.ReadUInt16LittleEndian(condBefore) > 0)
                    {
                        changed |= Stage(staged, effective, condBefore, conditionBytes);
                        ulong live = At(copy, liveOff);
                        changed |= Stage(staged, live, staged.Read(live, liveConditionBytes!.Length), liveConditionBytes);
                    }
                }
                if (changes.Morale is { } morale && offsets.Morale is { } moraleOffset)
                {
                    ulong address = At(copy, moraleOffset);
                    changed |= Stage(staged, address, staged.Read(address, 1), [(byte)morale]);
                }
                if (!changed) continue;
                copies++;
                touched.Add(uid);
            }
        });
        return new(touched.Count, copies);
    }

    void ValidateCopy(IProcessMemory source, InMatchCopy copy, int uid)
    {
        if (source.ReadUInt64(copy.Address) != moduleBase + offsets.PlayerVtable ||
            source.ReadUInt64(At(copy, offsets.Data)) != moduleBase + offsets.DataVtable ||
            source.ReadUInt64(At(copy, offsets.SourcePerson)) != copy.Source ||
            source.ReadUInt64(At(copy, offsets.Fixture)) != copy.Fixture ||
            source.ReadUInt64(At(copy, offsets.Side)) != copy.Side ||
            source.ReadUInt64(copy.Source) != copy.PersonVtable ||
            BinaryPrimitives.ReadInt32LittleEndian(source.Read(copy.Source + (ulong)offsets.PersonUid, sizeof(int))) != uid)
            throw new StaleObjectException("The match roster changed. Scan for matches again.");
    }

    static bool Stage(IProcessMemory staged, ulong address, byte[] before, byte[] next)
    {
        if (before.AsSpan().SequenceEqual(next)) return false;
        if (!staged.TryWrite(address, next)) throw new MemoryAccessException("Could not stage the match value");
        return true;
    }

    static ulong At(InMatchCopy copy, int offset) => checked(copy.Address + (ulong)offset);
    static ulong Word(byte[] bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at));
}
