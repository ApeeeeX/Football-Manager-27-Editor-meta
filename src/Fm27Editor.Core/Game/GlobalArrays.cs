using System.Buffers;
using System.Collections.Concurrent;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Game;

/// <summary>
/// The game-wide pointer arrays whose addresses sit at fixed distances from the clubs array in the module's data
/// block. FM Studio 1.1.1 finds the clubs array with an AOB signature (`FF D7 48 8B 1D ?? ?? ?? ?? 4C 8B 35 ?? ?? ?? ??
/// 4C 39 F3`) and derives the other slots from it: person -440, staff -368, referees -320, agents -32, teams +144
/// (bytes, relative to the clubs array start slot). The same relative layout is used on FM27 by that editor
/// (`ResolveDynamicPointersFM27`), so this class only has to find the anchor once per session.
/// </summary>
public sealed record GlobalArrays(ulong ClubsBegin, ulong ClubsEnd, ulong StaffBegin, ulong StaffEnd,
    ulong RefereesBegin, ulong RefereesEnd, ulong AgentsBegin, ulong AgentsEnd, ulong TeamsBegin, ulong TeamsEnd)
{
    public static int Count(ulong begin, ulong end) =>
        begin != 0 && end > begin && (end - begin) % 8 == 0 ? (int)((end - begin) / 8) : 0;

    public int ClubsCount => Count(ClubsBegin, ClubsEnd);
    public int StaffCount => Count(StaffBegin, StaffEnd);
    public int RefereeCount => Count(RefereesBegin, RefereesEnd);
    public int AgentCount => Count(AgentsBegin, AgentsEnd);
    public int TeamsCount => Count(TeamsBegin, TeamsEnd);
}

/// <summary>Finds the clubs-array anchor and the arrays derived from it; read only.</summary>
/// <summary>Profile <c>globalArrays</c>: the module slot holding the clubs array begin/end pointers, for builds
/// whose code the x86-64 anchor signature does not match (ARM64 Mac). Null when the build was not measured.</summary>
public sealed record GlobalArrayOffsets(uint ClubsSlotRva);

public static class GlobalArrayFinder
{
    /// <summary>call rdi; mov rbx,[rip+d]; mov r14,[rip+d]; cmp rbx,r14 — the pair of array slots FM Studio reads.</summary>
    static readonly byte[] Prefix = [0xFF, 0xD7, 0x48, 0x8B, 0x1D];
    static readonly byte?[] Signature =
        [0xFF, 0xD7, 0x48, 0x8B, 0x1D, null, null, null, null, 0x4C, 0x8B, 0x35, null, null, null, null, 0x4C, 0x39, 0xF3];
    static readonly ConcurrentDictionary<(ulong Base, ulong Size), (ulong MatchAt, ulong StartSlot, ulong EndSlot)> CachedClubsSlots = new();

    /// <summary>The module slots holding the clubs array begin and end pointers, or null when the signature is absent.</summary>
    public static (ulong StartSlot, ulong EndSlot)? FindClubsSlots(IProcessMemory memory, ulong moduleBase, ulong moduleSize)
    {
        if (moduleSize < (ulong)Signature.Length) return null;
        if (CachedClubsSlots.TryGetValue((moduleBase, moduleSize), out var cached))
        {
            var verify = new byte[Signature.Length];
            if (memory.TryRead(cached.MatchAt, verify) && MatchesAt(verify, 0))
                return (cached.StartSlot, cached.EndSlot);
            CachedClubsSlots.TryRemove((moduleBase, moduleSize), out _);
        }

        const int chunk = 1 << 22;
        var buffer = ArrayPool<byte>.Shared.Rent(chunk + Signature.Length);
        try
        {
            foreach (var (secStart, secSize) in ScanRanges(memory, moduleBase, moduleSize))
            {
                for (ulong offset = 0; offset < secSize; offset += chunk)
                {
                    int length = (int)Math.Min((ulong)chunk + (ulong)Signature.Length, secSize - offset);
                    if (length < Signature.Length) continue;
                    var span = buffer.AsSpan(0, length);
                    if (!memory.TryRead(secStart + offset, span)) continue;
                    int found = IndexOf(span);
                    if (found < 0) continue;
                    ulong at = secStart + offset + (ulong)found;
                    int disp1 = BitConverter.ToInt32(buffer, found + 5), disp2 = BitConverter.ToInt32(buffer, found + 12);
                    ulong start = at + 2 + 7 + (ulong)(long)disp1, end = at + 9 + 7 + (ulong)(long)disp2;
                    if (start < moduleBase || end < moduleBase || start >= moduleBase + moduleSize || end >= moduleBase + moduleSize)
                        continue;
                    CachedClubsSlots[(moduleBase, moduleSize)] = (at, start, end);
                    return (start, end);
                }
            }
            return null;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    static IEnumerable<(ulong Start, ulong Size)> ScanRanges(IProcessMemory memory, ulong moduleBase, ulong moduleSize)
    {
        if (moduleSize >= 0x400)
        {
            var dos = new byte[4];
            if (memory.TryRead(moduleBase + 0x3C, dos))
            {
                int peOffset = BitConverter.ToInt32(dos);
                var header = new byte[24];
                if (peOffset > 0 && (ulong)peOffset + 24 < moduleSize && memory.TryRead(moduleBase + (ulong)peOffset, header) &&
                    header[0] == (byte)'P' && header[1] == (byte)'E' && header[2] == 0 && header[3] == 0)
                {
                    int sections = BitConverter.ToUInt16(header, 6), optional = BitConverter.ToUInt16(header, 20);
                    var table = new byte[sections * 40];
                    if (sections > 0 && memory.TryRead(moduleBase + (ulong)peOffset + 24 + (ulong)optional, table))
                    {
                        var exec = new List<(ulong Start, ulong Size)>();
                        for (int i = 0; i < sections; i++)
                        {
                            uint size = BitConverter.ToUInt32(table, i * 40 + 8), rva = BitConverter.ToUInt32(table, i * 40 + 12);
                            uint flags = BitConverter.ToUInt32(table, i * 40 + 36);
                            if ((flags & 0x20000000) != 0 && rva + (ulong)size <= moduleSize)
                                exec.Add((moduleBase + rva, size));
                        }
                        if (exec.Count > 0) return exec;
                    }
                }
            }
        }
        return [(moduleBase, moduleSize)];
    }

    /// <summary>The clubs-array start slot: the profile's measured slot when the build has one, else the
    /// x86-64 code-signature anchor (which never matches on ARM64 Mac).</summary>
    public static ulong? StartSlot(IProcessMemory memory, ulong moduleBase, ulong moduleSize, uint? clubsSlotRva = null)
    {
        if (clubsSlotRva is { } rva)
        {
            if (!TryReadSlot(memory, moduleBase + rva, out ulong begin, out ulong end) || begin == 0) return null;
            return moduleBase + rva;
        }
        return FindClubsSlots(memory, moduleBase, moduleSize)?.StartSlot;
    }

    /// <summary>Reads the begin/end vector at each derived slot; null counts stay zero.</summary>
    public static GlobalArrays? Read(IProcessMemory memory, ulong moduleBase, ulong moduleSize, uint? clubsSlotRva = null) =>
        StartSlot(memory, moduleBase, moduleSize, clubsSlotRva) is { } start ? ReadAt(memory, start) : null;

    static bool TryReadSlot(IProcessMemory memory, ulong slot, out ulong begin, out ulong end)
    {
        var b = new byte[16];
        begin = 0; end = 0;
        if (!memory.TryRead(slot, b)) return false;
        begin = BitConverter.ToUInt64(b);
        end = BitConverter.ToUInt64(b, 8);
        return begin != 0 && end >= begin && (end - begin) % 8 == 0 && (end - begin) / 8 <= 1_000_000;
    }

    /// <summary>Reads the begin/end vector at each derived slot; null counts stay zero.</summary>
    public static GlobalArrays ReadAt(IProcessMemory memory, ulong clubsStartSlot)
    {
        ulong Q(ulong at)
        {
            Span<byte> b = stackalloc byte[8];
            return memory.TryRead(at, b) ? BitConverter.ToUInt64(b) : 0;
        }
        ulong clubs = clubsStartSlot;
        return new(ClubsBegin: Q(clubs), ClubsEnd: Q(clubs + 8),
            StaffBegin: Q(clubs - 368), StaffEnd: Q(clubs - 360),
            RefereesBegin: Q(clubs - 320), RefereesEnd: Q(clubs - 312),
            AgentsBegin: Q(clubs - 32), AgentsEnd: Q(clubs - 24),
            TeamsBegin: Q(clubs + 144), TeamsEnd: Q(clubs + 152));
    }

    static bool MatchesAt(ReadOnlySpan<byte> data, int at) =>
        data.Length - at >= Signature.Length &&
        data.Slice(at, Prefix.Length).SequenceEqual(Prefix) &&
        data[at + 9] == 0x4C && data[at + 10] == 0x8B && data[at + 11] == 0x35 &&
        data[at + 16] == 0x4C && data[at + 17] == 0x39 && data[at + 18] == 0xF3;

    static int IndexOf(ReadOnlySpan<byte> data)
    {
        int pos = 0;
        while (data.Length - pos >= Signature.Length)
        {
            int rel = data[pos..^14].IndexOf(Prefix);
            if (rel < 0) break;
            int hit = pos + rel;
            if (MatchesAt(data, hit)) return hit;
            pos = hit + 1;
        }
        return -1;
    }
}
