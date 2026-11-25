using System.Text;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Offsets;

/// <summary>How to find a static root slot again on a new build: the slot holds a vector whose elements carry
/// <paramref name="Vtable"/> at <paramref name="Offset"/>, or the slot points at one object carrying it.</summary>
public sealed record RootFormula(string Kind, string Vtable, int Offset, int Min, int Max)
{
    public const string Vector = "vector";
    public const string Object = "object";
}

public readonly record struct VectorCandidate(uint Rva, int Count, int MatchedVtables, bool IsPointerToVector);

public readonly record struct IndirectVectorCandidate(uint RootRva, int Offset, int Count, int MatchedVtables);

public readonly record struct DateWitness(DateOnly? MinDateInclusive, DateOnly? MaxDateInclusive);

public readonly record struct DateCandidate(int Offset, DateOnly Date, int WitnessMatches, int WitnessTotal, ushort Flags);

/// <summary>Static-root discovery for a new build. Scans use mapped image ranges plus bounded heap reads, so a
/// wrong profile cannot make them agree with themselves.</summary>
public static class RootFinder
{
    const ulong HeapLow = 0x1_0000_0000UL, HeapHigh = 0x7FFF_FFFF_FFFFUL;

    static bool IsHeap(ulong value) => value >= HeapLow && value < HeapHigh && (value & 0x7) == 0;

    static bool HasVtable(IProcessMemory memory, ulong address, ulong expected)
    {
        try { return memory.ReadUInt64(address) == expected; }
        catch (MemoryAccessException) { return false; }
    }

    static bool TryReadU64(IProcessMemory memory, ulong address, out ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        if (address != 0 && memory.TryRead(address, buf))
        {
            value = BitConverter.ToUInt64(buf);
            return true;
        }
        value = 0;
        return false;
    }

    static bool TryReadI32(IProcessMemory memory, ulong address, out int value)
    {
        Span<byte> buf = stackalloc byte[4];
        if (address != 0 && memory.TryRead(address, buf))
        {
            value = BitConverter.ToInt32(buf);
            return true;
        }
        value = 0;
        return false;
    }

    /// <summary>
    /// Scans writable module data for the inline vector allocator struct: header == 0, pooled == 1, pool vector
    /// holding [512..65536] pool pointers whose 16/64/256-byte pools have poolHeader == 0 and monotonic capacities.
    /// </summary>
    public static IReadOnlyList<(uint Rva, int PoolCount)> FindVectorAllocator(ModuleImage image, IProcessMemory memory,
        VectorAllocatorOffsets layout)
    {
        int structSize = Math.Max(layout.Pools + 24, Math.Max(layout.Header + 4, layout.Pooled + 1));
        var hits = new List<(uint Rva, int PoolCount)>();
        var bytes = image.Bytes;

        foreach (var (rangeStart, rangeEnd) in image.WritableDataRanges)
        {
            int start = Math.Max(0, (rangeStart + 7) & ~7);
            int endLimit = Math.Min(bytes.Length, rangeEnd);
            for (int slot = start; slot + structSize <= endLimit; slot += 8)
            {
                if (BitConverter.ToInt32(bytes, slot + layout.Header) != 0 || bytes[slot + layout.Pooled] != 1)
                    continue;
                ulong begin = BitConverter.ToUInt64(bytes, slot + layout.Pools);
                ulong end = BitConverter.ToUInt64(bytes, slot + layout.Pools + 8);
                ulong cap = BitConverter.ToUInt64(bytes, slot + layout.Pools + 16);
                if (!IsHeap(begin) || end <= begin || cap < end || (end - begin) % 8 != 0) continue;
                ulong count = (end - begin) / 8;
                if (count is < 512 or > 65536) continue;

                // Windows keeps one pool for sizes 1-16 (p1 == p16); macOS 27.1.x keeps one per size (capacity 8 at
                // index 1, 16 at index 16), so only the rising capacities are required.
                if (!TryReadU64(memory, begin + 1 * 8, out ulong p1) || !IsHeap(p1) ||
                    !TryReadU64(memory, begin + 16 * 8, out ulong p16) || !IsHeap(p16) ||
                    !TryReadU64(memory, begin + 64 * 8, out ulong p64) || !IsHeap(p64) ||
                    !TryReadU64(memory, begin + 256 * 8, out ulong p256) || !IsHeap(p256))
                    continue;

                if (!TryReadPool(p1, out int c1) || c1 < 1 || c1 > 16 ||
                    !TryReadPool(p16, out int c16) || c16 < 16 || c1 > c16 ||
                    !TryReadPool(p64, out int c64) || c64 < 64 ||
                    !TryReadPool(p256, out int c256) || c256 < 256 ||
                    c16 > c64 || c64 > c256 || c256 > (1 << 20))
                    continue;

                hits.Add(((uint)slot, (int)count));
            }
        }
        return hits;

        bool TryReadPool(ulong pool, out int capacity)
        {
            capacity = 0;
            return TryReadI32(memory, pool + (ulong)layout.PoolHeader, out int hdr) && hdr == 0 &&
                   TryReadI32(memory, pool + (ulong)layout.PoolCapacity, out capacity);
        }
    }

    /// <summary>Every module slot whose vector at +Offset holds objects with the expected vtable, in image order.</summary>
    public static IReadOnlyList<uint> FindVector(ModuleImage image, IProcessMemory memory, uint vtableRva, int offset,
        int min, int max)
    {
        ulong expected = image.ModuleBase + vtableRva;
        var found = new List<uint>();
        var bytes = image.Bytes;
        for (int slot = 0; slot + offset + 16 <= bytes.Length; slot += 8)
        {
            int header = slot + offset;
            ulong begin = BitConverter.ToUInt64(bytes, header), end = BitConverter.ToUInt64(bytes, header + 8);
            if (!IsHeap(begin) || end < begin || (end - begin) % 8 != 0) continue;
            ulong count = (end - begin) / 8;
            if (count < (ulong)min || count > (ulong)max) continue;
            if (!TryReadU64(memory, begin, out ulong first) || !HasVtable(memory, first, expected)) continue;
            found.Add((uint)slot);
        }
        return found;
    }

    /// <summary>
    /// Scans writable module data for <c>{begin, end}</c> pointer vectors (and optionally pointer-to-vector roots
    /// <c>*[slot] + vectorOffset</c>) whose elements carry any vtable in <paramref name="vtableRvas"/>.
    /// </summary>
    public static IReadOnlyList<VectorCandidate> FindVectors(ModuleImage image, IProcessMemory memory,
        IReadOnlySet<uint> vtableRvas, int min, int max, bool allowPointerToVector = false, int vectorOffset = 0)
    {
        if (vtableRvas.Count == 0) return [];
        var expectedAbs = vtableRvas.Select(v => image.ModuleBase + v).ToHashSet();
        var results = new Dictionary<uint, VectorCandidate>();
        var checkedPtrTargets = new Dictionary<ulong, (int Count, int MatchedVtables)>();
        var bytes = image.Bytes;

        foreach (var (rangeStart, rangeEnd) in image.WritableDataRanges)
        {
            int start = Math.Max(0, (rangeStart + 7) & ~7);
            int endLimit = Math.Min(bytes.Length, rangeEnd);
            for (int slot = start; slot + 8 <= endLimit; slot += 8)
            {
                if (vectorOffset == 0 && slot + 16 <= endLimit)
                {
                    ulong begin = BitConverter.ToUInt64(bytes, slot);
                    ulong end = BitConverter.ToUInt64(bytes, slot + 8);
                    int matched = CountVectorMatches(memory, begin, end, min, max, expectedAbs, out int count);
                    if (matched > 0)
                    {
                        results[(uint)slot] = new((uint)slot, count, matched, IsPointerToVector: false);
                        continue;
                    }
                }

                if (!allowPointerToVector) continue;
                ulong ptr = BitConverter.ToUInt64(bytes, slot);
                if (!IsHeap(ptr)) continue;
                if (!checkedPtrTargets.TryGetValue(ptr, out var cached))
                {
                    int matched = 0;
                    int count = 0;
                    if (TryReadU64(memory, ptr + (ulong)vectorOffset, out ulong b) &&
                        TryReadU64(memory, ptr + (ulong)vectorOffset + 8, out ulong e))
                    {
                        matched = CountVectorMatches(memory, b, e, min, max, expectedAbs, out count);
                    }
                    cached = (count, matched);
                    checkedPtrTargets[ptr] = cached;
                }
                if (cached.MatchedVtables > 0 && !results.ContainsKey((uint)slot))
                    results[(uint)slot] = new((uint)slot, cached.Count, cached.MatchedVtables, IsPointerToVector: true);
            }
        }

        return results.Values
            .OrderByDescending(x => x.MatchedVtables)
            .ThenBy(x => x.IsPointerToVector ? 1 : 0)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.Rva)
            .ToArray();
    }

    /// <summary>
    /// Scans writable module data for static pointers <c>holder = *[slot]</c> where <c>[holder + offset]</c> is a
    /// <c>{begin, end}</c> pointer vector of objects matching <paramref name="vtableRvas"/>.
    /// </summary>
    public static IReadOnlyList<IndirectVectorCandidate> FindIndirectVectors(ModuleImage image, IProcessMemory memory,
        IReadOnlySet<uint> vtableRvas, int maxOffset, int min, int max)
    {
        if (vtableRvas.Count == 0 || maxOffset < 0) return [];
        var expectedAbs = vtableRvas.Select(v => image.ModuleBase + v).ToHashSet();
        var bytes = image.Bytes;
        int alignedMax = (maxOffset + 7) & ~7;
        var holderCache = new Dictionary<ulong, List<(int Offset, int Count, int MatchedVtables)>>();
        var results = new List<IndirectVectorCandidate>();
        var probeBuf = new byte[alignedMax + 16];

        foreach (var (rangeStart, rangeEnd) in image.WritableDataRanges)
        {
            int start = Math.Max(0, (rangeStart + 7) & ~7);
            int endLimit = Math.Min(bytes.Length, rangeEnd);
            for (int slot = start; slot + 8 <= endLimit; slot += 8)
            {
                ulong holder = BitConverter.ToUInt64(bytes, slot);
                if (!IsHeap(holder)) continue;
                if (!holderCache.TryGetValue(holder, out var hits))
                {
                    hits = [];
                    if (memory.TryRead(holder, probeBuf))
                    {
                        for (int off = 0; off <= alignedMax; off += 8)
                        {
                            ulong begin = BitConverter.ToUInt64(probeBuf, off);
                            ulong end = BitConverter.ToUInt64(probeBuf, off + 8);
                            int matched = CountVectorMatches(memory, begin, end, min, max, expectedAbs, out int count);
                            if (matched > 0) hits.Add((off, count, matched));
                        }
                    }
                    holderCache[holder] = hits;
                }
                foreach (var (off, count, matched) in hits)
                    results.Add(new((uint)slot, off, count, matched));
            }
        }

        return results
            .OrderByDescending(x => x.MatchedVtables)
            .ThenByDescending(x => x.Count)
            .ThenBy(x => x.RootRva)
            .ThenBy(x => x.Offset)
            .ToArray();
    }

    static int CountVectorMatches(IProcessMemory memory, ulong begin, ulong end, int min, int max,
        HashSet<ulong> expectedAbs, out int count)
    {
        count = 0;
        if (!IsHeap(begin) || end < begin || (end - begin) % 8 != 0) return 0;
        ulong rawCount = (end - begin) / 8;
        if (rawCount < (ulong)min || rawCount > (ulong)max || rawCount == 0) return 0;
        count = (int)rawCount;

        int sampleSlots = Math.Min(count, 16);
        var ptrs = new byte[sampleSlots * 8];
        if (!memory.TryRead(begin, ptrs)) return 0;

        var seenVtables = new HashSet<ulong>();
        int nonNull = 0, matched = 0;
        for (int i = 0; i < sampleSlots; i++)
        {
            ulong obj = BitConverter.ToUInt64(ptrs, i * 8);
            if (obj == 0) continue;
            if (!IsHeap(obj)) return 0;
            nonNull++;
            if (TryReadU64(memory, obj, out ulong vt) && expectedAbs.Contains(vt))
            {
                matched++;
                seenVtables.Add(vt);
            }
        }
        if (matched == 0 || matched * 2 < nonNull) return 0;
        return seenVtables.Count;
    }

    /// <summary>Every module slot that points at an object carrying the expected vtable, in image order.</summary>
    public static IReadOnlyList<uint> FindObject(ModuleImage image, IProcessMemory memory, uint vtableRva) =>
        FindObjects(image, memory, vtableRva);

    /// <summary>Every module slot in writable data that points at an object carrying the expected vtable.</summary>
    public static IReadOnlyList<uint> FindObjects(ModuleImage image, IProcessMemory memory, uint vtableRva)
    {
        ulong expected = image.ModuleBase + vtableRva;
        var found = new List<uint>();
        var bytes = image.Bytes;
        foreach (var (rangeStart, rangeEnd) in image.WritableDataRanges)
        {
            int start = Math.Max(0, (rangeStart + 7) & ~7);
            int endLimit = Math.Min(bytes.Length, rangeEnd);
            for (int slot = start; slot + 8 <= endLimit; slot += 8)
            {
                ulong target = BitConverter.ToUInt64(bytes, slot);
                if (!IsHeap(target)) continue;
                if (!HasVtable(memory, target, expected)) continue;
                found.Add((uint)slot);
            }
        }
        return found;
    }

    /// <summary>
    /// Disambiguates multiple primary (offset 0) RTTI vtables for the same class name (for example Itanium construction
    /// vtables vs complete-object vtables) by sampling live singletons and vectors reachable from writable data.
    /// </summary>
    public static IReadOnlyList<uint> DisambiguateVtables(ModuleImage image, IProcessMemory memory,
        IReadOnlyList<uint> candidateVtables, int minVectorCount = 1, int maxVectorCount = 500_000)
    {
        if (candidateVtables.Count <= 1) return candidateVtables;
        var candidateAbs = candidateVtables.Distinct().ToDictionary(v => image.ModuleBase + v, v => v);
        var hits = new Dictionary<uint, int>();
        var bytes = image.Bytes;
        var singletons = new List<ulong>();

        // Pass 1: static vectors and candidate singleton pointers in writable data.
        foreach (var (rangeStart, rangeEnd) in image.WritableDataRanges)
        {
            int start = Math.Max(0, (rangeStart + 7) & ~7);
            int endLimit = Math.Min(bytes.Length, rangeEnd);
            for (int slot = start; slot + 8 <= endLimit; slot += 8)
            {
                ulong p = BitConverter.ToUInt64(bytes, slot);
                if (!IsHeap(p)) continue;
                singletons.Add(p);

                if (slot + 16 <= endLimit)
                {
                    ulong end = BitConverter.ToUInt64(bytes, slot + 8);
                    if (end >= p && (end - p) % 8 == 0)
                    {
                        ulong count = (end - p) / 8;
                        if (count >= (ulong)Math.Max(1, minVectorCount) && count <= (ulong)maxVectorCount)
                            TallyVectorSample(memory, p, (int)count, candidateAbs, hits);
                    }
                }
            }
        }

        // Batch-check dereferenced targets of static pointers for singleton matches.
        var uniqueSingletons = singletons.Distinct().ToArray();
        TallyPointerTargets(memory, uniqueSingletons, candidateAbs, hits);
        if (hits.Count > 0)
            return hits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray();

        // Pass 2: 1-hop holder objects (for DATABASE sub-lists like COMP, NATION, STADIUM, AWARD).
        var holderProbe = new byte[0x2400];
        foreach (var holder in uniqueSingletons.Take(256))
        {
            if (!memory.TryRead(holder, holderProbe)) continue;
            for (int off = 0; off + 16 <= holderProbe.Length; off += 8)
            {
                ulong begin = BitConverter.ToUInt64(holderProbe, off);
                ulong end = BitConverter.ToUInt64(holderProbe, off + 8);
                if (!IsHeap(begin) || end < begin || (end - begin) % 8 != 0) continue;
                ulong count = (end - begin) / 8;
                if (count is < 20 or > 200_000) continue;
                TallyVectorSample(memory, begin, (int)count, candidateAbs, hits);
            }
        }
        return hits.Count > 0
            ? hits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray()
            : candidateVtables;
    }

    static void TallyVectorSample(IProcessMemory memory, ulong begin, int count,
        Dictionary<ulong, uint> candidateAbs, Dictionary<uint, int> hits)
    {
        int sampleCount = Math.Min(count, 8);
        var buf = new byte[sampleCount * 8];
        if (!memory.TryRead(begin, buf)) return;
        for (int i = 0; i < sampleCount; i++)
        {
            ulong elem = BitConverter.ToUInt64(buf, i * 8);
            if (!IsHeap(elem)) continue;
            if (TryReadU64(memory, elem, out ulong vt) && candidateAbs.TryGetValue(vt, out uint rva))
                hits[rva] = hits.GetValueOrDefault(rva) + 1;
        }
    }

    static void TallyPointerTargets(IProcessMemory memory, ulong[] pointers,
        Dictionary<ulong, uint> candidateAbs, Dictionary<uint, int> hits)
    {
        if (pointers.Length == 0) return;
        if (memory is IScatterReadMemory scatter)
        {
            const int batchSize = 512;
            var ok = new bool[batchSize];
            var slots = new byte[batchSize][];
            for (int b = 0; b < batchSize; b++) slots[b] = new byte[8];
            for (int i = 0; i < pointers.Length; i += batchSize)
            {
                int n = Math.Min(batchSize, pointers.Length - i);
                var reqs = new MemoryReadRequest[n];
                for (int j = 0; j < n; j++) reqs[j] = new(pointers[i + j], slots[j]);
                scatter.ReadMany(reqs, ok.AsSpan(0, n));
                for (int j = 0; j < n; j++)
                {
                    if (!ok[j]) continue;
                    ulong vt = BitConverter.ToUInt64(slots[j]);
                    if (candidateAbs.TryGetValue(vt, out uint rva))
                        hits[rva] = hits.GetValueOrDefault(rva) + 1;
                }
            }
            return;
        }

        foreach (ulong ptr in pointers)
            if (TryReadU64(memory, ptr, out ulong vt) && candidateAbs.TryGetValue(vt, out uint rva))
                hits[rva] = hits.GetValueOrDefault(rva) + 1;
    }

    /// <summary>
    /// Disambiguates subobject vtables (for example person parts embedded inside staff/player entries) using live
    /// parent objects at <paramref name="subobjectOffset"/>.
    /// </summary>
    public static IReadOnlyList<uint> DisambiguateSubobjectVtable(IProcessMemory memory, ulong moduleBase,
        IReadOnlyList<ulong> sampleParents, int subobjectOffset, IReadOnlyList<uint> candidateVtables)
    {
        if (candidateVtables.Count <= 1 || sampleParents.Count == 0 || subobjectOffset < 0)
            return candidateVtables;
        var candidateAbs = candidateVtables.Distinct().ToDictionary(v => moduleBase + v, v => v);
        var hits = new Dictionary<uint, int>();
        foreach (ulong parent in sampleParents.Take(64))
        {
            if (TryReadU64(memory, parent + (ulong)subobjectOffset, out ulong vt) &&
                candidateAbs.TryGetValue(vt, out uint rva))
            {
                hits[rva] = hits.GetValueOrDefault(rva) + 1;
            }
        }
        return hits.Count > 0
            ? hits.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => kv.Key).ToArray()
            : candidateVtables;
    }

    /// <summary>
    /// Scans an in-memory manager object for packed 4-byte FM dates (<c>uint16 dayOfYear, int16 year</c>) and
    /// scores each candidate against contract/injury witness bounds.
    /// </summary>
    public static IReadOnlyList<DateCandidate> FindGameDateCandidates(IProcessMemory memory, ulong objectAddress,
        int scanStart, int scanEnd, IReadOnlyList<DateWitness> witnesses)
    {
        if (objectAddress == 0 || scanEnd <= scanStart + 4) return [];
        var raw = new byte[scanEnd - scanStart];
        if (!memory.TryRead(objectAddress + (ulong)scanStart, raw)) return [];
        var results = new List<DateCandidate>();
        for (int rel = 0; rel + 4 <= raw.Length; rel += 2)
        {
            ushort rawDay = BitConverter.ToUInt16(raw, rel);
            ushort flags = (ushort)(rawDay & 0xFE00);
            int day = rawDay & 0x1FF;
            short year = BitConverter.ToInt16(raw, rel + 2);
            if (year is < 2020 or > 2100 || day < 1 || day > (DateTime.IsLeapYear(year) ? 366 : 365))
                continue;
            var candidateDate = new DateOnly(year, 1, 1).AddDays(day - 1);
            int matches = 0;
            foreach (var w in witnesses)
            {
                if (w.MinDateInclusive is { } min && candidateDate < min) continue;
                if (w.MaxDateInclusive is { } max && candidateDate > max) continue;
                matches++;
            }
            results.Add(new(scanStart + rel, candidateDate, matches, witnesses.Count, flags));
        }
        return results
            .OrderByDescending(c => c.WitnessMatches)
            .ThenBy(c => c.Flags == 0 ? 0 : 1)
            .ThenBy(c => Math.Abs(c.Offset - 0x7FC))
            .ToArray();
    }

    /// <summary>RVA of the build string ("27.0.0+2493426") in the image, found by shape rather than by a stored RVA.</summary>
    public static uint? FindBuildString(ModuleImage image)
    {
        var bytes = image.Bytes;
        foreach (var (rangeStart, rangeEnd) in image.StringRanges)
        {
            int start = Math.Max(0, rangeStart);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int i = start; i + 8 < end; i++)
            {
                if (bytes[i] < (byte)'0' || bytes[i] > (byte)'9') continue;
                int length = 0;
                while (length < 40 && i + length < end && IsBuildChar(bytes[i + length])) length++;
                if (length < 8 || i + length >= end || bytes[i + length] != 0) continue;
                string text = Encoding.ASCII.GetString(bytes, i, length);
                var parts = text.Split('.', '+');
                if (parts.Length != 4 || parts[0] != "27") continue;
                if (!parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit))) continue;
                return (uint)i;
            }
        }
        return null;
    }

    public static string? ReadAsciiZ(ModuleImage image, uint rva, int maxLen = 64)
    {
        int start = (int)rva;
        if (start < 0 || start >= image.Bytes.Length) return null;
        int end = Array.IndexOf(image.Bytes, (byte)0, start, Math.Min(maxLen, image.Bytes.Length - start));
        if (end <= start) return null;
        return Encoding.ASCII.GetString(image.Bytes, start, end - start);
    }

    static bool IsBuildChar(byte b) => b is >= (byte)'0' and <= (byte)'9' || b == (byte)'.' || b == (byte)'+';
}
