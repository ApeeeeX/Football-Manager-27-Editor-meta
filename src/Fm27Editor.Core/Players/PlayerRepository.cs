using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Players;

/// <summary>Enumerates the game's player list from its static root in game_plugin.dll.</summary>
public sealed class PlayerRepository(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
{
    const int MaxPlayers = 2_000_000;

    public IReadOnlyList<PlayerRecord> LoadAll()
    {
        var pointers = ReadPointerArray();
        if (memory is IConcurrentMemoryReads && pointers.Length >= 2048)
        {
            int workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
            int chunk = (pointers.Length + workerCount - 1) / workerCount;
            var parts = new List<PlayerRecord>[workerCount];
            Parallel.For(0, workerCount, i =>
            {
                int start = i * chunk;
                int count = Math.Min(chunk, pointers.Length - start);
                if (count <= 0) { parts[i] = []; return; }
                using var reads = new BulkReadScope(memory, pageCount: 2048);
                parts[i] = new PlayerRepository(reads.Memory, profile, moduleBase).LoadSlice(pointers, start, count);
            });
            var combined = new List<PlayerRecord>(pointers.Length);
            foreach (var part in parts) combined.AddRange(part);
            return combined;
        }
        using var serialReads = new BulkReadScope(memory);
        return new PlayerRepository(serialReads.Memory, profile, moduleBase).LoadSlice(pointers, 0, pointers.Length);
    }

    List<PlayerRecord> LoadSlice(ulong[] pointers, int start, int count)
    {
        var slice = pointers.AsSpan(start, count);
        var reader = new PlayerReader(memory, profile) { ModuleBase = moduleBase };
        var vtables = profile.PlayerVtableRvas.Select(v => moduleBase + v).ToHashSet();
        if (memory is ReadAheadMemory ram)
        {
            int insertionTail = profile.PlayerInsertions.Count > 0 ? profile.PlayerInsertions.Values.Max(i => i.Length) : 0;
            ram.Prefetch(slice, profile.PlayerObjectSize + insertionTail);
            var f = profile.Fields;
            var namePointers = new List<ulong>(count * 2);
            var seenNames = new HashSet<ulong>();
            var contracts = profile.Contract is not null ? new List<ulong>(count) : null;
            Span<byte> word = stackalloc byte[8];
            static int Shift(PlayerInsertion ins, bool hasIns, int offset) =>
                hasIns && offset >= ins.At ? offset + ins.Length : offset;
            static void AddNamePtr(IProcessMemory mem, ulong addr, List<ulong> targets, HashSet<ulong> seen, Span<byte> buf)
            {
                if (mem.TryRead(addr, buf))
                {
                    ulong ptr = BitConverter.ToUInt64(buf);
                    if (ptr != 0 && seen.Add(ptr)) targets.Add(ptr);
                }
            }
            foreach (ulong p in slice)
            {
                if (p == 0 || !memory.TryRead(p, word)) continue;
                ulong vt = BitConverter.ToUInt64(word);
                if (!vtables.Contains(vt)) continue;
                uint rva = (uint)(vt - moduleBase);
                bool hasIns = profile.PlayerInsertions.TryGetValue(rva, out var ins);
                if (contracts is not null &&
                    memory.TryRead(p + (ulong)Shift(ins, hasIns, profile.Contract!.PlayerContract), word))
                {
                    ulong ptr = BitConverter.ToUInt64(word);
                    if (ptr != 0) contracts.Add(ptr);
                }
                if (f.FullName >= 0) AddNamePtr(memory, p + (ulong)Shift(ins, hasIns, f.FullName), namePointers, seenNames, word);
                if (f.FirstName >= 0) AddNamePtr(memory, p + (ulong)Shift(ins, hasIns, f.FirstName), namePointers, seenNames, word);
                if (f.LastName >= 0) AddNamePtr(memory, p + (ulong)Shift(ins, hasIns, f.LastName), namePointers, seenNames, word);
                if (f.CommonName >= 0) AddNamePtr(memory, p + (ulong)Shift(ins, hasIns, f.CommonName), namePointers, seenNames, word);
            }
            if (contracts is { Count: > 0 })
                ram.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(contracts), 0x60);
            if (namePointers.Count > 0)
                ram.Prefetch(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(namePointers), 64);
        }
        var obj = new byte[profile.PlayerObjectSize];
        var result = new List<PlayerRecord>(count);
        foreach (var p in slice)
        {
            if (p == 0 || !memory.TryRead(p, obj)) continue;
            ulong vt = BitConverter.ToUInt64(obj);
            if (!vtables.Contains(vt)) continue;
            uint rva = (uint)(vt - moduleBase);
            if (ReadSubtypeTail(p, rva, obj)) result.Add(reader.Decode(p, obj, rva));
        }
        return result;
    }

    /// <summary>Re-reads one player, e.g. after a write or to refresh a stale snapshot.</summary>
    public PlayerRecord? Reload(ulong address)
    {
        var obj = new byte[profile.PlayerObjectSize];
        if (!memory.TryRead(address, obj)) return null;
        ulong vt = BitConverter.ToUInt64(obj);
        if (!profile.PlayerVtableRvas.Any(v => moduleBase + v == vt)) return null;
        uint rva = (uint)(vt - moduleBase);
        if (!ReadSubtypeTail(address, rva, obj)) return null;
        return new PlayerReader(memory, profile) { ModuleBase = moduleBase }.Decode(address, obj, rva);
    }

    // Decode in canonical field order, without reading beyond the shorter record type.
    bool ReadSubtypeTail(ulong address, uint rva, Span<byte> obj) =>
        !profile.PlayerInsertions.TryGetValue(rva, out var insertion) ||
        memory.TryRead(checked(address + (ulong)insertion.At + (ulong)insertion.Length), obj[insertion.At..]);

    ulong[] ReadPointerArray()
    {
        ulong root = moduleBase + profile.PlayerListRootRva;
        ulong begin = memory.ReadUInt64(root + (ulong)profile.PlayerListBeginOffset);
        ulong end = memory.ReadUInt64(root + (ulong)profile.PlayerListEndOffset);
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > MaxPlayers)
            throw new MemoryAccessException($"Player list root looks invalid (begin 0x{begin:X}, end 0x{end:X}); is a save loaded?");
        var raw = memory.Read(begin, (int)(end - begin));
        var pointers = new ulong[raw.Length / 8];
        Buffer.BlockCopy(raw, 0, pointers, 0, raw.Length);
        return pointers;
    }
}
