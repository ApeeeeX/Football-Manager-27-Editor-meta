using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Clubs;

/// <summary>Read-only discovery in the already identified transfer manager; uncertainty retains the heap fallback.</summary>
internal static class OwnedTransferDeals
{
    const int MaxOffers = 100_000;

    public static IReadOnlyList<ulong>? Read(IProcessMemory memory, OffsetProfile profile, ulong moduleBase,
        IReadOnlyList<ulong> dealVtables)
    {
        if (profile.Proposals is not { ManagerRva: { } root, ManagerVtable: { } managerType } o ||
            o.OfferVector < 0 || o.OfferVector + 24 > o.OfferSerial || o.OfferSerial > 4092) return null;
        int size = new[] { 0x90, o.Id + 4, o.Person + 8 }.Max();
        if (size > 4096 || o.Id < 0 || o.Person < 0) return null;
        Span<byte> pointer = stackalloc byte[8];
        if (!memory.TryRead(moduleBase + root, pointer)) return null;
        ulong manager = BitConverter.ToUInt64(pointer);
        var owner = new byte[o.OfferSerial + 4];
        if (manager < 0x10000 || !memory.TryRead(manager, owner) || BitConverter.ToUInt64(owner) != moduleBase + managerType) return null;
        ulong[] types = [moduleBase + o.TransferVtable, moduleBase + o.LoanVtable, moduleBase + o.TrialVtable, moduleBase + o.BClubMoveVtable];
        var snapshots = new Dictionary<ulong, byte[]>();
        if (!Vector(owner.AsSpan(o.OfferVector, 24), out var begin, out var count) || count == 0) return null;
        var primary = new byte[count * 8];
        if (!memory.TryRead(begin, primary)) return null;
        using var scope = new BulkReadScope(memory);
        for (int id = 0; id < count; id++)
        {
            ulong address = BitConverter.ToUInt64(primary, id * 8);
            if (address == 0) continue;
            var record = new byte[size];
            if (!scope.Memory.TryRead(address, record) || !types.Contains(BitConverter.ToUInt64(record)) ||
                BitConverter.ToInt32(record, o.Id) != id || !snapshots.TryAdd(address, record)) return null;
        }
        if (snapshots.Count == 0) return null;

        // The manager owns a second collection of offer copies. Discover it by its complete relationship
        // to the indexed table, rather than assuming an offset from a particular build. Every copy must
        // have a distinct address, a valid source ID, the same concrete type and the same person pointer.
        int selected = -1;
        byte[]? copies = null;
        ulong copiesBegin = 0;
        Dictionary<ulong, byte[]>? copyRecords = null;
        for (int offset = 8; offset + 24 <= owner.Length; offset += 8)
        {
            if (offset == o.OfferVector || !Vector(owner.AsSpan(offset, 24), out ulong candidateBegin, out int candidateCount) || candidateCount == 0) continue;
            var pointers = new byte[candidateCount * 8];
            if (!scope.Memory.TryRead(candidateBegin, pointers)) return null;
            bool invalid = false, unknown = false;
            var records = new Dictionary<ulong, byte[]>();
            for (int slot = 0; slot < candidateCount; slot++)
            {
                ulong address = BitConverter.ToUInt64(pointers, slot * 8);
                if (address == 0 && offset == o.CopyVector) continue;
                if (address < 0x10000 || (address & 7) != 0 || snapshots.ContainsKey(address) || records.ContainsKey(address)) { invalid = true; break; }
                var head = new byte[8];
                if (!scope.Memory.TryRead(address, head)) { unknown = true; continue; }
                if (!types.Contains(BitConverter.ToUInt64(head))) { invalid = true; break; }
                var record = new byte[size];
                if (!scope.Memory.TryRead(address, record)) { unknown = true; continue; }
                ulong vt = BitConverter.ToUInt64(record);
                if (!types.Contains(vt)) { invalid = true; break; }
                int id = BitConverter.ToInt32(record, o.Id);
                if (id < 0 || id >= count || !snapshots.TryGetValue(BitConverter.ToUInt64(primary, id * 8), out var source) ||
                    BitConverter.ToUInt64(source) != vt || BitConverter.ToUInt64(source, o.Person) != BitConverter.ToUInt64(record, o.Person))
                { invalid = true; break; }
                records.Add(address, record);
            }
            if (invalid) continue;
            if (unknown || selected >= 0) return null;
            selected = offset; copies = pointers; copiesBegin = candidateBegin; copyRecords = records;
        }
        // An empty copy collection has no structural identity unless its slot is explicitly verified by the profile.
        if (selected >= 0 && o.CopyVector is { } expectedCopy && selected != expectedCopy) return null;
        if (selected < 0 && o.CopyVector is { } copyOff && copyOff >= 8 && copyOff + 24 <= owner.Length && copyOff != o.OfferVector)
        {
            ulong b0 = BitConverter.ToUInt64(owner, copyOff), e0 = BitConverter.ToUInt64(owner, copyOff + 8), c0 = BitConverter.ToUInt64(owner, copyOff + 16);
            bool validEmpty = (b0 == 0 && e0 == 0 && c0 == 0) || (Vector(owner.AsSpan(copyOff, 24), out _, out int emptyCount) && emptyCount == 0);
            if (validEmpty) { selected = copyOff; copies = []; copiesBegin = 0; copyRecords = []; }
        }
        if (selected < 0 || copies is null || copyRecords is null) return null;
        foreach (var entry in copyRecords) snapshots.Add(entry.Key, entry.Value);
        if (!memory.TryRead(moduleBase + root, pointer) || BitConverter.ToUInt64(pointer) != manager ||
            !Same(manager, owner) ||
            !Same(manager + (ulong)o.OfferVector, owner.AsSpan(o.OfferVector, 24)) ||
            !Same(manager + (ulong)selected, owner.AsSpan(selected, 24)) || !Same(begin, primary) ||
            (copiesBegin != 0 && !Same(copiesBegin, copies))) return null;
        // Recheck uncached records: a cached page must never certify a live mutation as stable.
        foreach (var (address, record) in snapshots)
            if (!Same(address, record)) return null;
        return snapshots.Where(p => dealVtables.Contains(BitConverter.ToUInt64(p.Value))).Select(p => p.Key).Order().ToArray();

        bool Same(ulong address, ReadOnlySpan<byte> expected)
        {
            var current = new byte[expected.Length];
            return memory.TryRead(address, current) && current.AsSpan().SequenceEqual(expected);
        }
    }

    static bool Vector(ReadOnlySpan<byte> header, out ulong begin, out int count)
    {
        begin = BitConverter.ToUInt64(header); count = 0;
        ulong end = BitConverter.ToUInt64(header[8..]), capacity = BitConverter.ToUInt64(header[16..]);
        if (begin < 0x10000 || (begin & 7) != 0 || end < begin || capacity < end ||
            (end - begin) % 8 != 0 || (capacity - begin) % 8 != 0 || (capacity - begin) / 8 > MaxOffers) return false;
        count = (int)((end - begin) / 8);
        return true;
    }
}
