using System.Buffers.Binary;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.CodeGen;

/// <summary>
/// One AArch64 instruction word to look for, with the bits that must match. A masked word lets a site pattern ignore
/// register numbers (which the compiler may renumber between builds) while still pinning the opcode and the immediate,
/// e.g. the field offset a load reads through. <see cref="Value"/> may carry register numbers the mask drops.
/// </summary>
public readonly record struct Arm64Word(uint Value, uint Mask = uint.MaxValue)
{
    public bool Matches(uint word) => (word & Mask) == (Value & Mask);
}

/// <summary>Cached raw bytes of executable <c>__TEXT</c> sections for sharing across multiple hook-site lookups.</summary>
public sealed class Arm64CodeSnapshot
{
    internal readonly IReadOnlyList<(ulong Start, byte[] Bytes)> Sections;
    internal Arm64CodeSnapshot(IReadOnlyList<(ulong Start, byte[] Bytes)> sections) => Sections = sections;
}

/// <summary>
/// Finds the instruction sequence of a hook site in the image's own <c>__text</c>.
/// </summary>
/// <remarks>
/// The x86-64 presets find their sites with byte patterns read out of the PE section table. On macOS the image is a
/// Mach-O and its code sits in a section of the <c>__TEXT</c> segment instead, so this class reads that segment's
/// section table, scans the executable sections four bytes at a time and requires exactly one match — a missing or
/// ambiguous site stops a patch rather than guessing, exactly as on Windows.
/// </remarks>
public static class Arm64SiteFinder
{
    const uint MachMagic64 = 0xFEEDFACF, LcSegment64 = 0x19;
    const int MachHeader64Size = 32, SegmentCommand64Size = 72, Section64Size = 80;
    const uint SAttrPureInstructions = 0x8000_0000, SAttrSomeInstructions = 0x0000_0400;
    const int MaxLoadCommands = 16 * 1024 * 1024;

    /// <summary>Executable sections of the image's <c>__TEXT</c> segment, as (address, size) ranges inside the module.</summary>
    public static IEnumerable<(ulong Start, ulong Size)> CodeSections(IProcessMemory memory, ulong moduleBase, ulong moduleSize)
    {
        var header = memory.Read(moduleBase, MachHeader64Size);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != MachMagic64)
            throw new InvalidOperationException($"0x{moduleBase:X} does not hold a 64-bit Mach-O header");

        uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint commandsSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        if (commandsSize == 0 || commandsSize > MaxLoadCommands)
            throw new InvalidOperationException($"Invalid Mach-O load command size {commandsSize} at 0x{moduleBase:X}");
        var commands = memory.Read(moduleBase + MachHeader64Size, (int)commandsSize);

        var sections = new List<(ulong, ulong)>();
        int offset = 0;
        for (uint i = 0; i < commandCount && offset + 8 <= commands.Length; i++)
        {
            uint command = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(offset));
            uint commandSize = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(offset + 4));
            if (commandSize < 8 || offset + (long)commandSize > commands.Length) break;
            if (command == LcSegment64 && commandSize >= SegmentCommand64Size)
            {
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(offset + 64));
                int at = offset + SegmentCommand64Size;
                for (uint s = 0; s < count && at + Section64Size <= offset + commandSize; s++, at += Section64Size)
                {
                    uint flags = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(at + 64));
                    if ((flags & (SAttrPureInstructions | SAttrSomeInstructions)) == 0) continue;
                    ulong address = BinaryPrimitives.ReadUInt64LittleEndian(commands.AsSpan(at + 32));
                    ulong size = BinaryPrimitives.ReadUInt64LittleEndian(commands.AsSpan(at + 40));
                    if (size < 4 || address + size > moduleSize) continue;
                    sections.Add((moduleBase + address, size));
                }
            }
            offset += (int)commandSize;
        }
        if (sections.Count == 0) throw new InvalidOperationException("The image has no executable __TEXT sections");
        return sections;
    }

    /// <summary>
    /// The one address in the executable code holding <paramref name="pattern"/>, or <paramref name="patched"/> instead
    /// (a site already carrying a jump, whose remaining instructions still match the pattern from <c>patchedFrom</c>).
    /// </summary>
    /// <param name="pattern">Instructions to match, all four bytes significant unless masked.</param>
    /// <param name="patched">True when the word at a hit is already a jump, so the site is returned rather than counted twice.</param>
    /// <param name="patchedFrom">Index of the first pattern word that stays in place under the patch.</param>
    /// <param name="accept">
    /// The last word on a hit: the matched instruction words and their address. A pattern that leaves register numbers
    /// free uses it to require agreement between them (the words of a patched site are no longer at the site, so the
    /// address lets the caller read them back from its own block).
    /// </param>
    /// <exception cref="InvalidOperationException">No match, more than one match, or a hit that is not 4-byte aligned.</exception>
    /// <summary>Reads all executable <c>__TEXT</c> sections once so several hooks can search them without repeated IPC reads; null on failure.</summary>
    public static Arm64CodeSnapshot? TryCaptureCode(IProcessMemory memory, ulong moduleBase, ulong moduleSize)
    {
        try
        {
            var sections = new List<(ulong Start, byte[] Bytes)>();
            foreach (var (start, size) in CodeSections(memory, moduleBase, moduleSize))
            {
                if (size > 512UL * 1024 * 1024) return null;
                var bytes = new byte[size];
                int off = 0;
                while (off < bytes.Length)
                {
                    int take = Math.Min(1 << 22, bytes.Length - off);
                    if (!memory.TryRead(start + (ulong)off, bytes.AsSpan(off, take))) return null;
                    off += take;
                }
                sections.Add((start, bytes));
            }
            return new Arm64CodeSnapshot(sections);
        }
        catch (Exception ex) when (ex is InvalidOperationException or MemoryAccessException)
        {
            return null;
        }
    }

    /// <summary>Verifies whether a previously located instruction sequence still sits at <paramref name="at"/>.</summary>
    public static bool TryVerifyAt(IProcessMemory memory, ulong at, IReadOnlyList<Arm64Word> pattern,
        Func<uint, bool>? patched = null, int patchedFrom = 0, Func<uint[], ulong, bool>? accept = null)
    {
        if (at == 0 || (at & 3) != 0 || pattern.Count == 0 || pattern.Count > 16) return false;
        Span<byte> raw = stackalloc byte[pattern.Count * 4];
        if (!memory.TryRead(at, raw)) return false;
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(raw);
        bool matches = MatchesWords(span, pattern, 0);
        if (!matches && (patched is null || !patched(span[0]) || !MatchesWords(span, pattern, patchedFrom)))
            return false;
        return accept is null || accept(ReadWordsFromSpan(span), at);
    }

    public static ulong Find(IProcessMemory memory, ulong moduleBase, ulong moduleSize, string feature,
        IReadOnlyList<Arm64Word> pattern, Func<uint, bool>? patched = null, int patchedFrom = 0,
        Func<uint[], ulong, bool>? accept = null, Arm64CodeSnapshot? snapshot = null)
    {
        if (pattern.Count == 0) throw new ArgumentException("An empty pattern matches everywhere", nameof(pattern));
        var hits = new List<ulong>();
        int bytes = pattern.Count * 4;
        if (snapshot is not null)
        {
            foreach (var (start, sectionBytes) in snapshot.Sections)
            {
                if (sectionBytes.Length < bytes) continue;
                ScanSpan(start, sectionBytes, sectionBytes.Length - bytes + 4, pattern, patched, patchedFrom, accept, hits);
                if (hits.Count > 1) break;
            }
        }
        else
        {
            foreach (var (start, size) in CodeSections(memory, moduleBase, moduleSize))
            {
                const int Chunk = 1 << 22;
                var buffer = new byte[Chunk + bytes];
                for (ulong offset = 0; offset < size; offset += Chunk)
                {
                    int length = (int)Math.Min((ulong)buffer.Length, size - offset);
                    if (length < bytes) continue;
                    if (!memory.TryRead(start + offset, buffer.AsSpan(0, length))) continue;
                    int limitBytes = Math.Min(Chunk, length - bytes + 4);
                    ScanSpan(start + offset, buffer.AsSpan(0, length), limitBytes, pattern, patched, patchedFrom, accept, hits);
                    if (hits.Count > 1) break;
                }
                if (hits.Count > 1) break;
            }
        }
        if (hits.Count == 0) throw new InvalidOperationException($"{feature}: the code was not found in this build");
        if (hits.Count > 1) throw new InvalidOperationException($"{feature}: the code is not unique in this build");
        return hits[0];
    }

    static void ScanSpan(
        ulong baseAddress,
        ReadOnlySpan<byte> span,
        int limitBytes,
        IReadOnlyList<Arm64Word> pattern,
        Func<uint, bool>? patched,
        int patchedFrom,
        Func<uint[], ulong, bool>? accept,
        List<ulong> hits)
    {
        int wordLimit = limitBytes / 4;
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(span);
        uint mask0 = pattern[0].Mask;
        uint val0 = pattern[0].Value & mask0;
        int count = pattern.Count;
        for (int w = 0; w < wordLimit; w++)
        {
            uint w0 = words[w];
            if ((w0 & mask0) == val0)
            {
                if (MatchesWords(words[w..], pattern, 1))
                {
                    ulong here = baseAddress + (ulong)w * 4;
                    if ((here & 3) == 0 && (accept is null || accept(ReadWordsFromSpan(words.Slice(w, count)), here)))
                        hits.Add(here);
                    if (hits.Count > 1) return;
                }
                continue;
            }
            if (patched is null || !patched(w0)) continue;
            if (!MatchesWords(words[w..], pattern, patchedFrom)) continue;
            ulong patchedHere = baseAddress + (ulong)w * 4;
            if ((patchedHere & 3) == 0 && (accept is null || accept(ReadWordsFromSpan(words.Slice(w, count)), patchedHere)))
                hits.Add(patchedHere);
            if (hits.Count > 1) return;
        }
    }

    static bool MatchesWords(ReadOnlySpan<uint> words, IReadOnlyList<Arm64Word> pattern, int from)
    {
        for (int i = from; i < pattern.Count; i++)
            if (!pattern[i].Matches(words[i])) return false;
        return true;
    }

    static uint[] ReadWordsFromSpan(ReadOnlySpan<uint> words)
    {
        var read = new uint[words.Length];
        words.CopyTo(read);
        return read;
    }
}
