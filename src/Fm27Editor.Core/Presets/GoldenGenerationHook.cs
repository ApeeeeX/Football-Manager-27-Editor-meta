using System.Buffers.Binary;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Presets;

/// <summary>The settings the regen routine works with, as the game process holds them.</summary>
/// <summary>What the id list in the settings means: the new player's club, his club's nation, or that nation's continent.</summary>
public enum GoldenGenerationScope : byte { All = 0, Club = 1, Nation = 2, Continent = 3 }

public sealed record GoldenGenerationSettings(bool Enabled, int MinPa, int MaxPa, int WonderkidChance, GoldenGenerationScope Scope, IReadOnlyList<int> Ids);

/// <summary>One pass of the game through the regen site: the new player and the registers around it.</summary>
public sealed record GoldenGenerationHit(ulong Player, ulong Rbx, ulong Rdi, ulong R12, ulong R13, ulong R14, ulong R15, ulong Club)
{
    /// <summary>The game's stack at the site, from rsp up, 56 qwords.</summary>
    public IReadOnlyList<ulong> Stack { get; init; } = [];
}

/// <summary>Whether the hook is in place, how often it ran, and what it does.</summary>
public sealed record GoldenGenerationState(ulong Site, ulong Cave, long Calls, long Raised, int Wonderkids,
    GoldenGenerationSettings Settings, IReadOnlyList<GoldenGenerationHit> Recent);

/// <summary>
/// Golden Generation as a hook: when the game creates a new player (a regen) it sets his CA and PA from the generated
/// values, then reads PA again to count it into its PA buckets (<c>movsx eax, word [rsi+PA]; mov ecx, 9</c>). A jump
/// there leads to a routine in the game process that, when switched on, gives the new player a random PA in the chosen
/// range (never below his CA, never above 200) and, with the wonderkid chance, lifts his CA part of the way to the new PA.
/// It can be limited to regens of chosen clubs, of clubs from chosen nations, or of clubs from chosen continents. The hook lives in the game: once installed it keeps working with the
/// editor closed, until the game exits.
/// </summary>
/// <remarks>
/// <para>Idea from the Cheat Engine table's Golden Generation (FM26), which hooks the same PA read. The routine is written
/// here; the PA draw and the wonderkid CA lift follow the same rules (wonderkid only when CA is below 70% of PA; CA moves
/// 30/40/55/70% of the gap for PA below 120/150/180/above). The site is found by its bytes, built from the profile's PA
/// offset; exactly one match is required.</para>
/// <para><c>rdi</c> at the site is the generated-player record the game copies CA and PA from: club at <c>+0x78</c>, the
/// club's nation at <c>+0x68</c>; the nation's continent is at the profile's nation continent offset, and each of these
/// objects has its unique id at <c>+0xC</c> (Man Utd 680, England 765, Europe 3). Every pass is also written to a ring of the last 64 (player, six registers, the record's club and the stack) so the club of a new player can be worked
/// out from real intakes.</para>
/// </remarks>
public sealed class GoldenGenerationHook(IProcessMemory memory, ICodePatchHost? host, OffsetProfile profile, ulong moduleBase, int moduleSize)
{
    public const int MaxIds = 32, RingSize = 64;

    /// <summary>PA is 1-200 in the game; a draw is capped here exactly as the game's own repair caps it.</summary>
    public const int MaxPa = 200;
    internal const int MagicOffset = 0x00, CallsOffset = 0x08, RaisedOffset = 0x10, OriginalOffset = 0x18, SiteOffset = 0x20,
        EnabledOffset = 0x28, MinPaOffset = 0x2A, RangeOffset = 0x2C, WonderkidOffset = 0x2E, NationCountOffset = 0x30,
        ScopeOffset = 0x2F, RingIndexOffset = 0x38, WonderkidCountOffset = 0x3C, CodeOffset = 0x40, NationsOffset = 0x300, RingOffset = 0x400,
        RecordSize = 0x200, StackWords = 56;
    public const int BlockSize = 0x10000;

    /// <summary>
    /// The mutable part of the block (magic, settings, nation list, ring): never executed, so a W^X host keeps it
    /// read/write. The routine writes the ring and the counters on every pass. ARM64 puts it below <see cref="CodePage"/>
    /// so only the code page is ever made read/execute; x86-64 keeps the original interleaved layout.
    /// </summary>
    const int DataSize = 0xC000;

    /// <summary>Where the ARM64 routine starts: 48 KB (three 16 KB pages) above the block base, leaving the whole block 64 KB.</summary>
    const int CodePage = 0xC000;

    /// <summary>The smallest page an ARM64 host can protect (16 KB on Apple Silicon), also the ARM64 code slot.</summary>
    const int Arm64PageSize = 0x4000;

    static int CodeBaseOf(bool arm64) => arm64 ? CodePage : CodeOffset;
    static int BlockBytesOf(bool arm64) => arm64 ? CodePage + Arm64PageSize : BlockSize;

    ulong CodeBase => (ulong)CodeBaseOf(profile.IsArm64);
    int BlockBytes => BlockBytesOf(profile.IsArm64);

    /// <summary>How many bytes the routine may use: up to the nation list on x86-64, the whole page on ARM64.</summary>
    int CodeSlot => profile.IsArm64 ? Arm64PageSize : NationsOffset - (int)CodeBase;

    /// <summary>Makes the routine's slot executable (the whole page on ARM64, where the block reserves one).</summary>
    void CommitRoutine(ulong cave) => host!.CommitExecutable(cave + CodeBase, CodeSlot);

    internal static readonly byte[] Magic = "FM27GLD3"u8.ToArray();
    internal const int StolenLengthX64 = 7, StolenLengthArm64 = 8;

    /// <summary>Bytes the jump replaces: seven on x86-64 (<c>movsx eax, word [rsi+PA]; mov ecx, 9</c>); on ARM64 the two
    /// whole instructions <c>ldrsh w?, [x20+PA]</c> and <c>cmp w?, #0xb3</c>.</summary>
    int StolenLength => profile.IsArm64 ? StolenLengthArm64 : StolenLengthX64;
    const uint PageExecuteReadWrite = 0x40;
    const uint CaSentinel = 0x11223344, PaSentinel = 0x55667788, ContinentSentinel = 0x33445566;

    ulong? site;

    /// <summary>Bytes at the site: <c>movsx eax, word [rsi+PA]; mov ecx, 9</c>. The first seven are replaced.</summary>
    public static byte[] OriginalPattern(OffsetProfile profile)
    {
        var p = new List<byte> { 0x0F, 0xBF, 0x86 };
        p.AddRange(BitConverter.GetBytes(profile.Fields.PotentialAbility));
        p.AddRange([0xB9, 0x09, 0x00, 0x00, 0x00]);
        return [.. p];
    }

    /// <summary>
    /// The ARM64 site inside <c>regeneration_manager</c>: <c>ldrsh w?, [x20+PA]; cmp w?, #0xb3; b.le; mov w?, #9</c> —
    /// the top PA-bucket check before attribute generation. <c>x20</c> is <c>ACTUAL_PLAYER</c> and <c>x19</c> is the
    /// youth-candidate record (matching <c>rsi</c>/<c>rdi</c> on x86-64). The two stolen words are re-run after setting
    /// <c>PA</c> and <c>CA</c>, so the following <c>b.le</c> sees the boosted PA and downstream attribute generation uses
    /// the boosted CA/PA.
    /// </summary>
    Arm64Word[] Arm64Pattern() =>
    [
        new(0x79C00000u | ((uint)(Fields.PotentialAbility / 2) << 10) | (20u << 5) | 8u, BaseRegisterOnly),  // ldrsh w?, [x20+PA]
        new(0x7102CD1Fu, 0xFFFFFC1Fu),                                                                       // cmp w?, #0xb3
        new(0x5400000Du, 0xFF00001Fu),                                                                       // b.le
        new(0x52800128u, 0xFFFFFFE0u),                                                                       // mov w?, #9
    ];

    PlayerFieldOffsets Fields => profile.Fields;

    TeamOffsets Team => profile.Team ?? throw new InvalidOperationException("The profile has no team offsets");
    ClubOffsets Club => profile.Club ?? throw new InvalidOperationException("The profile has no club offsets");

    static bool Arm64IsBranchWord(uint word) => (word & 0xFC000000) == 0x14000000;

    /// <summary>
    /// Register-consistency and patched-block check for the ARM64 site. Unpatched, all three PA-bucket instructions
    /// (<c>ldrsh</c>, <c>cmp</c>, <c>mov #9</c>) must use the same <c>w?</c> register. Patched, the first two words
    /// are a <c>B</c> and <c>NOP</c>, so the stolen instructions are read back from the block's header and checked for
    /// this hook's <c>FM27GLD</c> magic, this exact site, and matching registers with <c>words[3]</c>. A block that
    /// cannot be read is accepted, since nothing can be said about it.
    /// </summary>
    bool Accept(uint[] words, ulong at)
    {
        uint movReg = words[3] & 0x1Fu;
        if (!Arm64IsBranchWord(words[0]))
        {
            uint loadReg = words[0] & 0x1Fu;
            uint cmpReg = (words[1] >> 5) & 0x1Fu;
            return loadReg == cmpReg && loadReg == movReg;
        }
        ulong routine = at + (ulong)((long)((int)(words[0] << 6) >> 6) * 4);
        var head = new byte[Math.Max(SiteOffset + sizeof(ulong), OriginalOffset + StolenLengthArm64)];
        if (!memory.TryRead(routine - CodeBase, head)) return true;
        if (!head.AsSpan(MagicOffset, Magic.Length - 1).SequenceEqual(Magic.AsSpan(0, Magic.Length - 1))) return false;
        if (BitConverter.ToUInt64(head, SiteOffset) != at) return false;
        var pattern = Arm64Pattern();
        uint orig0 = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(OriginalOffset));
        uint orig1 = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(OriginalOffset + 4));
        if (!pattern[0].Matches(orig0) || !pattern[1].Matches(orig1)) return false;
        uint origLoadReg = orig0 & 0x1Fu;
        uint origCmpReg = (orig1 >> 5) & 0x1Fu;
        return origLoadReg == origCmpReg && origLoadReg == movReg;
    }

    /// <summary>Everything but the destination register, so a rebuild may renumber where a load writes.</summary>
    const uint BaseRegisterOnly = 0xFFFF_FFE0;

    /// <summary>The bytes the site must hold before patching, in the profile's instruction set.</summary>
    byte[] OriginalBytes() => profile.IsArm64
        ? Arm64Pattern().SelectMany(w => BitConverter.GetBytes(w.Value)).ToArray()
        : OriginalPattern(profile).AsSpan(0, StolenLengthX64).ToArray();

    /// <summary>
    /// True when the stolen bytes still are the site's original code: whole bytes on x86-64, and on ARM64 the masked
    /// words only, since a register number or a branch distance may legitimately differ from the recorded pattern.
    /// </summary>
    bool SiteHolds(ReadOnlySpan<byte> at)
    {
        if (!profile.IsArm64) return at.SequenceEqual(OriginalBytes());
        var pattern = Arm64Pattern();
        for (int i = 0; i < StolenLengthArm64 / 4; i++)
            if (!pattern[i].Matches(BinaryPrimitives.ReadUInt32LittleEndian(at[(i * 4)..]))) return false;
        return true;
    }

    bool IsPatched(ReadOnlySpan<byte> at) =>
        profile.IsArm64 ? at.Length >= 4 && Arm64IsBranchWord(BinaryPrimitives.ReadUInt32LittleEndian(at)) : at[0] == 0xE9;

    /// <summary>The cave a jump at <paramref name="site"/> points into.</summary>
    ulong CaveOf(ulong site, ReadOnlySpan<byte> at) => profile.IsArm64
        ? site + (ulong)((long)((int)(BinaryPrimitives.ReadUInt32LittleEndian(at) << 6) >> 6) * 4) - CodeBase
        : site + 5 + (ulong)(long)BitConverter.ToInt32(at[1..]) - CodeBase;

    static bool IsPatchedSite(ReadOnlySpan<byte> at, byte[] original) =>
        at.Length >= original.Length && at[0] == 0xE9 && at[5] == 0x90 && at[6] == 0x90
        && at[7..original.Length].SequenceEqual(original.AsSpan(7));

    /// <summary>The ARM64 form of <see cref="IsPatchedSite"/>: a <c>B</c> and a NOP where the two stolen loads were.</summary>
    static bool IsPatchedSiteArm64(ReadOnlySpan<byte> at, byte[] original) =>
        at.Length >= original.Length && Arm64IsBranchWord(BinaryPrimitives.ReadUInt32LittleEndian(at))
        && BinaryPrimitives.ReadUInt32LittleEndian(at[4..]) == 0xD503201F
        && at[8..original.Length].SequenceEqual(original.AsSpan(8));

    /// <summary>The site: the one place in the module's code holding <see cref="OriginalPattern"/>, patched or not.</summary>
    public ulong FindSite() => FindSite(null, null);

    /// <summary>Finds the site using an optional pre-captured <c>__TEXT</c> snapshot on ARM64 or a verified site hint.</summary>
    public ulong FindSite(CodeGen.Arm64CodeSnapshot? snapshot, ulong? hintedSite = null)
    {
        if (site is { } known) return known;
        if (profile.IsArm64)
        {
            var pattern = Arm64Pattern();
            if (hintedSite is { } hint &&
                Arm64SiteFinder.TryVerifyAt(memory, hint, pattern, Arm64IsBranchWord, patchedFrom: 2, Accept))
                return (site = hint).Value;
            // A patched site starts with the B and the NOP that replaced the two stolen loads, so the match resumes at
            // the third word.
            return (site = Arm64SiteFinder.Find(memory, moduleBase, (ulong)moduleSize, Feature,
                pattern, Arm64IsBranchWord, patchedFrom: 2, Accept, snapshot)).Value;
        }
        var original = OriginalPattern(profile);
        var hits = new List<ulong>();
        foreach (var (start, size) in CodeSections())
        {
            const int Chunk = 1 << 20;
            var buffer = new byte[Chunk + original.Length];
            for (ulong offset = 0; offset < size; offset += Chunk)
            {
                int length = (int)Math.Min((ulong)buffer.Length, size - offset);
                if (!memory.TryRead(start + offset, buffer.AsSpan(0, length))) continue;
                var span = buffer.AsSpan(0, length);
                int limit = Math.Min(Chunk, length - original.Length + 1);
                for (int i = 0; i < limit; i++)
                {
                    if (span[i] != 0x0F && span[i] != 0xE9) continue;
                    var here = span[i..];
                    if (here[..original.Length].SequenceEqual(original) || IsPatchedSite(here, original))
                        hits.Add(start + offset + (ulong)i);
                }
                if (hits.Count > 1) break;
            }
            if (hits.Count > 1) break;
        }
        if (hits.Count == 0) throw new InvalidOperationException($"{Feature}: the regen code was not found in this build");
        if (hits.Count > 1) throw new InvalidOperationException($"{Feature}: the regen code is not unique in this build");
        return (site = hits[0]).Value;
    }

    const string Feature = "Golden Generation";

    IEnumerable<(ulong Start, ulong Size)> CodeSections() => profile.IsArm64
        ? Arm64SiteFinder.CodeSections(memory, moduleBase, (ulong)moduleSize)
        : PeCodeSections();

    IEnumerable<(ulong Start, ulong Size)> PeCodeSections()
    {
        int peOffset = BitConverter.ToInt32(memory.Read(moduleBase + 0x3C, 4));
        var header = memory.Read(moduleBase + (ulong)peOffset, 24);
        int sections = BitConverter.ToUInt16(header, 6), optional = BitConverter.ToUInt16(header, 20);
        var table = memory.Read(moduleBase + (ulong)peOffset + 24 + (ulong)optional, sections * 40);
        for (int i = 0; i < sections; i++)
        {
            uint size = BitConverter.ToUInt32(table, i * 40 + 8), rva = BitConverter.ToUInt32(table, i * 40 + 12);
            uint flags = BitConverter.ToUInt32(table, i * 40 + 36);
            if ((flags & 0x20000000) == 0 || rva + (ulong)size > (ulong)moduleSize) continue;
            yield return (moduleBase + rva, size);
        }
    }

    /// <summary>The current state, or null when the hook is not installed.</summary>
    public GoldenGenerationState? Read()
    {
        ulong at = FindSite();
        var bytes = memory.Read(at, StolenLength);
        if (!IsPatched(bytes)) return null;
        ulong cave = CaveOf(at, bytes);
        var head = memory.Read(cave, RingOffset + RingSize * RecordSize);
        if (IsOlderRoutine(head, at)) return null;     // an earlier editor version's hook: Install replaces it
        if (!head.AsSpan(MagicOffset, Magic.Length).SequenceEqual(Magic) || BitConverter.ToUInt64(head, SiteOffset) != at)
            throw new InvalidOperationException("The regen code is patched by another tool");
        int count = BitConverter.ToInt32(head, NationCountOffset);
        if (count is < 0 or > MaxIds) throw new InvalidOperationException("The Wonderkid Academy id list is damaged");
        int min = BitConverter.ToInt16(head, MinPaOffset), range = BitConverter.ToUInt16(head, RangeOffset);
        var ids = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(head, NationsOffset + i * 4)).ToList();
        var settings = new GoldenGenerationSettings(head[EnabledOffset] != 0, min, min + Math.Max(range, 1) - 1, head[WonderkidOffset], (GoldenGenerationScope)head[ScopeOffset], ids);
        uint ring = BitConverter.ToUInt32(head, RingIndexOffset);
        var recent = new List<GoldenGenerationHit>();
        for (int i = 1; i <= (int)Math.Min(ring, (uint)RingSize); i++)
        {
            int r = RingOffset + (int)((ring - (uint)i) % RingSize) * RecordSize;
            ulong Q(int k) => BitConverter.ToUInt64(head, r + k * 8);
            recent.Add(new(Q(0), Q(1), Q(2), Q(3), Q(4), Q(5), Q(6), Q(7)) { Stack = Enumerable.Range(8, StackWords).Select(Q).ToList() });
        }
        return new(at, cave, BitConverter.ToInt64(head, CallsOffset), BitConverter.ToInt64(head, RaisedOffset),
            BitConverter.ToInt32(head, WonderkidCountOffset), settings, recent);
    }

    /// <summary>
    /// A block an earlier version of this editor wrote ("FM27GLD" with another version digit, same header layout up to the
    /// stolen bytes and the site): its settings are not carried over, since their meaning changed between versions.
    /// </summary>
    static bool IsOlderRoutine(byte[] head, ulong site) =>
        head.AsSpan(MagicOffset, Magic.Length - 1).SequenceEqual(Magic.AsSpan(0, Magic.Length - 1))
        && head[MagicOffset + Magic.Length - 1] != Magic[^1] && BitConverter.ToUInt64(head, SiteOffset) == site;

    /// <summary>Puts the game's bytes back over an older version's jump, so a new routine can be installed.</summary>
    void RemoveOlderRoutine(ulong at)
    {
        var bytes = memory.Read(at, StolenLength);
        if (!IsPatched(bytes) || host is null) return;
        ulong cave = CaveOf(at, bytes);
        var head = memory.Read(cave, CodeOffset);
        if (!IsOlderRoutine(head, at)) return;
        var original = head.AsSpan(OriginalOffset, StolenLength).ToArray();
        using (host.Pause())
        {
            uint previous = host.Protect(at, StolenLength, PageExecuteReadWrite);
            try { Put(at, original); }
            finally { host.Protect(at, StolenLength, previous); }
            host.FlushCode(at, StolenLength);
        }
    }

    /// <summary>Puts the jump and the routine in place, switched off, once per game run; returns the state.</summary>
    public GoldenGenerationState Install()
    {
        if (Read() is { } installed) return installed;
        if (host is null) throw new InvalidOperationException("Golden Generation needs a writable game process");
        ulong at = FindSite();
        RemoveOlderRoutine(at);
        var original = memory.Read(at, StolenLength);
        if (!SiteHolds(original))
            throw new StaleObjectException("The regen code changed while it was being read");
        ulong cave = host.AllocateNear(at, BlockBytes);
        var block = BuildBlock(cave, at, original, profile);
        Put(cave, block);
        host.FlushCode(cave, block.Length);
        CommitRoutine(cave);
        var patch = PatchBytes(at, cave + CodeBase);
        using (host.Pause())
        {
            if (!memory.Read(at, StolenLength).AsSpan().SequenceEqual(original))
                throw new StaleObjectException("The regen code changed before the patch");
            uint previous = host.Protect(at, StolenLength, PageExecuteReadWrite);
            try { Put(at, patch); }
            finally { host.Protect(at, StolenLength, previous); }
            host.FlushCode(at, StolenLength);
        }
        return Read() ?? throw new MemoryAccessException("The patch did not stay in place");
    }

    /// <summary>Puts the game's bytes back. The block stays allocated: a paused thread may still be in it.</summary>
    public void Uninstall()
    {
        if (Read() is not { } state || host is null) return;
        var original = memory.Read(state.Cave + OriginalOffset, StolenLength);
        using (host.Pause())
        {
            uint previous = host.Protect(state.Site, StolenLength, PageExecuteReadWrite);
            try { Put(state.Site, original); }
            finally { host.Protect(state.Site, StolenLength, previous); }
            host.FlushCode(state.Site, StolenLength);
        }
    }

    /// <summary>
    /// Switches the routine off and keeps its settings, so a new save is not shaped by choices made for another one
    /// (club, nation and continent IDs are the same in every save of a database). True when it was on.
    /// </summary>
    public bool SwitchOff()
    {
        if (Read() is not { Settings.Enabled: true } state) return false;
        Put(state.Cave + EnabledOffset, [0]);
        return true;
    }

    /// <summary>
    /// Switches the routine on with a PA range (1-200), a wonderkid chance (0-100%) and a scope: every regen, or only those
    /// whose club, club nation or continent has one of the given unique ids (up to <see cref="MaxIds"/>). Or off. Installs
    /// the hook on first use. The routine is switched off while the settings are written and switched on last, so it never
    /// works with half-written settings.
    /// </summary>
    public GoldenGenerationState Configure(bool enabled, int minPa, int maxPa, int wonderkidChance, GoldenGenerationScope scope, IReadOnlyList<int> ids)
    {
        if (minPa is < 1 or > 200 || maxPa is < 1 or > 200 || minPa > maxPa)
            throw new ArgumentOutOfRangeException(nameof(minPa), "PA range must be within 1-200 with min <= max");
        if (wonderkidChance is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(wonderkidChance), "Wonderkid chance must be 0-100");
        if (ids.Count > MaxIds) throw new ArgumentOutOfRangeException(nameof(ids), $"At most {MaxIds} entries");
        if (scope == GoldenGenerationScope.All && ids.Count > 0) throw new ArgumentException("A list needs a club, nation or continent scope", nameof(ids));
        if (scope != GoldenGenerationScope.All && ids.Count == 0) throw new ArgumentException("Choose at least one club, nation or continent", nameof(ids));
        var state = Install();
        PutBlock(state.Cave + EnabledOffset, [0]);
        var settings = new byte[6];
        BitConverter.GetBytes((short)minPa).CopyTo(settings, 0);
        BitConverter.GetBytes((ushort)(maxPa - minPa + 1)).CopyTo(settings, 2);
        settings[4] = (byte)wonderkidChance;
        settings[5] = (byte)scope;
        PutBlock(state.Cave + MinPaOffset, settings);
        var list = new byte[MaxIds * 4];
        for (int i = 0; i < ids.Count; i++) BitConverter.GetBytes(ids[i]).CopyTo(list, i * 4);
        PutBlock(state.Cave + NationsOffset, list);
        PutBlock(state.Cave + NationCountOffset, BitConverter.GetBytes(ids.Count));
        if (enabled) PutBlock(state.Cave + EnabledOffset, [1]);
        return Read()!;
    }

    void Put(ulong address, byte[] bytes)
    {
        if (!memory.TryWrite(address, bytes) || !memory.Read(address, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new MemoryAccessException($"Could not write 0x{address:X}");
    }

    /// <summary>Writes into the block, which is read/execute once installed; see the note on the accelerate routine's
    /// counterpart. The switches bracket the single write and the code is flushed afterwards.</summary>
    void PutBlock(ulong address, byte[] bytes)
    {
        if (host is null) { Put(address, bytes); return; }
        uint previous = host.Protect(address, bytes.Length, PageExecuteReadWrite);
        try { Put(address, bytes); }
        finally { host.Protect(address, bytes.Length, previous); }
        host.FlushCode(address, bytes.Length);
    }

    /// <summary><c>jmp rel32</c> and two nops over the seven stolen bytes.</summary>
    public static byte[] BuildPatch(ulong from, ulong to)
    {
        long delta = (long)to - (long)(from + 5);
        if (delta is < int.MinValue or > int.MaxValue) throw new InvalidOperationException("The routine is out of reach of a rel32 jump");
        var patch = new byte[StolenLengthX64];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)delta).CopyTo(patch, 1);
        patch[5] = patch[6] = 0x90;
        return patch;
    }

    /// <summary>The jump for the profile's instruction set: a <c>B</c> and a NOP on ARM64, a rel32 jump on x86-64.</summary>
    byte[] PatchBytes(ulong from, ulong to) => profile.IsArm64
        ? Arm64CodePatch.BuildPatch(from, to, StolenLengthArm64)
        : BuildPatch(from, to);

    /// <summary>The whole block: header (switched off, range 1), routine, empty nation list and ring.</summary>
    public static byte[] BuildBlock(ulong cave, ulong site, byte[] stolen, OffsetProfile profile)
    {
        bool arm64 = profile.IsArm64;
        int stolenLength = arm64 ? StolenLengthArm64 : StolenLengthX64;
        if (stolen.Length != stolenLength) throw new ArgumentException($"{stolenLength} stolen bytes are expected", nameof(stolen));
        int codeBase = CodeBaseOf(arm64);
        int slot = arm64 ? Arm64PageSize : NationsOffset - CodeOffset;
        var block = new byte[BlockBytesOf(arm64)];
        Magic.CopyTo(block, MagicOffset);
        stolen.CopyTo(block, OriginalOffset);
        BitConverter.GetBytes(site).CopyTo(block, SiteOffset);
        BitConverter.GetBytes((ushort)1).CopyTo(block, RangeOffset);
        var code = arm64
            ? BuildRoutineArm64(cave + (ulong)codeBase, cave, site + StolenLengthArm64, stolen, profile)
            : BuildRoutine(cave + (ulong)codeBase, cave, site + StolenLengthX64, stolen, profile);
        if (code.Length > slot)
            throw new InvalidOperationException($"The routine does not fit its slot ({code.Length} > {slot} bytes)");
        code.CopyTo(block, codeBase);
        return block;
    }

    /// <summary>
    /// The routine: save flags and rax, rcx, rdx, r8–r10; point rcx at the block; run <see cref="Body"/>; restore; run the
    /// stolen <c>movsx eax, word [rsi+PA]</c>; jump back. The body (assembled once; it has no absolute addresses):
    /// <code>
    /// inc qword [rcx+Calls]; store rsi, rbx, rdi, r12, r13, r14, r15, [rdi+0x78] (club) and 56 qwords of the game's stack at ring[RingIndex % 64]; inc dword [rcx+RingIndex]
    /// cmp byte [rcx+Enabled],0; je done
    /// if dword [rcx+NationCount] != 0:
    ///     rax = scope Club: [rdi+0x78] | Nation: [rdi+0x68] | Continent: [[rdi+0x68]+continent]; test rax; jz done; eax = [rax+0xC]
    ///     look eax up in the id list; not found: done
    /// r8d = word [rcx+Range]; jz done; rdtsc; edx = 0; div r8d; dx += [rcx+MinPa]; dx = min(dx, 200)
    /// dx = max(dx, word [rsi+CA]); word [rsi+PA] = dx; inc qword [rcx+Raised]
    /// r10d = byte [rcx+Wonderkid]; jz done; rdtsc; edx = 0; r8d = 100; div r8d; edx >= r10d: done
    /// r9d = PA; r10d = CA; eax = PA*70/100; CA >= eax: done
    /// r8d = 30 | 40 | 55 | 70 for PA &lt; 120 | &lt; 150 | &lt; 180 | else
    /// eax = CA + (PA-CA)*r8d/100, at most PA; word [rsi+CA] = ax; inc dword [rcx+Wonderkids]
    /// done:
    /// </code>
    /// The CA, PA and nation-continent displacements are placeholders filled from the profile.
    /// </summary>
    internal static byte[] BuildRoutine(ulong at, ulong cave, ulong resume, byte[] stolen, OffsetProfile profile)
    {
        var code = new List<byte> { 0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52 };   // pushfq; push rax, rcx, rdx, r8, r9, r10
        code.AddRange([0x48, 0x8D, 0x0D]);                                                            // lea rcx,[rip+cave]
        code.AddRange(BitConverter.GetBytes(checked((int)((long)cave - (long)(at + (ulong)code.Count + 4)))));
        var body = (byte[])Body.Clone();
        Replace(body, CaSentinel, (uint)profile.Fields.CurrentAbility, 3);
        Replace(body, PaSentinel, (uint)profile.Fields.PotentialAbility, 2);
        Replace(body, ContinentSentinel, (uint)(profile.Nation?.Continent ?? throw new InvalidOperationException("The profile has no nation continent offset")), 1);
        code.AddRange(body);
        code.AddRange([0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D]);                // pop r10, r9, r8, rdx, rcx, rax; popfq
        code.AddRange(stolen);
        code.Add(0xE9);                                                                               // jmp back
        code.AddRange(BitConverter.GetBytes(checked((int)((long)resume - (long)(at + (ulong)code.Count + 4)))));
        return [.. code];
    }

    /// <summary>
    /// The ARM64 routine, the counterpart of <see cref="Body"/>: <c>x20</c> is the new <c>ACTUAL_PLAYER</c> and
    /// <c>x19</c> is the youth-candidate record. It records the pass in the ring, then (when switched on and the
    /// candidate's club/nation/continent is in the id list) draws a PA in the range, lifts PA to his CA if need be, caps
    /// it at 200 and stores it; with the wonderkid chance it also lifts CA towards the new PA. The two stolen words
    /// (<c>ldrsh w?, [x20+PA]; cmp w?, #0xb3</c>) are re-run afterwards, so the game's <c>b.le</c> sees the new PA.
    /// </summary>
    /// <remarks>
    /// The registers the game has live at the site are never written: everything the routine uses is saved on the stack
    /// (x0-x7 and the flags) and restored before the stolen instructions. Scope ids come from the youth-candidate
    /// record (<c>club</c> / <c>basedNation</c>, falling back to <c>club-&gt;nation</c>), because <c>ACTUAL_PLAYER.Team</c>
    /// is zeroed earlier in <c>regeneration_manager</c>; <c>rdtsc</c> has no ARM64 form, so random draws use the virtual
    /// counter.
    /// </remarks>
    internal static byte[] BuildRoutineArm64(ulong at, ulong cave, ulong resume, byte[] stolen, OffsetProfile profile)
    {
        var club = profile.Club ?? throw new InvalidOperationException("The profile has no club offsets");
        int ca = profile.Fields.CurrentAbility, pa = profile.Fields.PotentialAbility;
        int clubNation = club.Nation ?? throw new InvalidOperationException("The profile has no club nation offset");
        int continent = profile.Nation?.Continent ?? throw new InvalidOperationException("The profile has no nation continent offset");
        int recordClub = profile.YouthCandidates?.Club ?? 0x78;
        int recordNation = profile.YouthCandidates?.BasedNation ?? 0x68;

        var a = new Arm64Assembler(at);
        a.StpPre(A64.X0, A64.X1, A64.SP, -0x50);                       // save the scratch registers and the flags
        a.Stp(A64.X2, A64.X3, A64.SP, 0x10);
        a.Stp(A64.X4, A64.X5, A64.SP, 0x20);
        a.Stp(A64.X6, A64.X7, A64.SP, 0x30);
        a.MrsNzcv(A64.X0);
        a.StrImm(A64.X0, A64.SP, 0x40);
        a.Adrp(A64.X0, cave);                                          // x0 = [block]
        a.AddImm(A64.X0, A64.X0, (int)(cave & 0xFFF));
        a.LdrImm(A64.X1, A64.X0, CallsOffset);
        a.AddImm(A64.X1, A64.X1, 1);
        a.StrImm(A64.X1, A64.X0, CallsOffset);                         // inc qword [block+Calls]
        a.LdrWord(A64.X1, A64.X0, RingIndexOffset);
        a.AndLowBits(A64.X1, A64.X1, 6);                               // RingSize is 64
        a.AddImm(A64.X2, A64.X0, RingOffset);
        a.AddRegLsl(A64.X2, A64.X2, A64.X1, 9);                        // record = ring[RingIndex & 63], 0x200 bytes each
        a.StrImm(A64.X20, A64.X2, 0x00);                               // ACTUAL_PLAYER (matching rsi on x86-64)
        a.StrImm(A64.X19, A64.X2, 0x08);                               // YouthCandidate (matching rdi on x86-64)
        a.StrImm(A64.X21, A64.X2, 0x10);
        a.StrImm(A64.X22, A64.X2, 0x18);
        a.StrImm(A64.X23, A64.X2, 0x20);
        a.StrImm(A64.X24, A64.X2, 0x28);
        a.StrImm(A64.X25, A64.X2, 0x30);
        a.Movz(A64.X3, 0);
        a.Cbz64(A64.X19, "noClub");
        a.LdrImm(A64.X3, A64.X19, recordClub);                         // record[7] = [x19+club]
        a.Label("noClub");
        a.StrImm(A64.X3, A64.X2, 0x38);
        a.AddImm(A64.X4, A64.SP, 0x50);                                // the game's stack, from its own top
        a.AddImm(A64.X5, A64.X2, 0x40);
        a.Movz(A64.X6, StackWords);
        a.Label("copy");
        a.LdrImm(A64.X7, A64.X4, 0);
        a.StrImm(A64.X7, A64.X5, 0);
        a.AddImm(A64.X4, A64.X4, 8);
        a.AddImm(A64.X5, A64.X5, 8);
        a.SubImm32(A64.X6, A64.X6, 1);
        a.Cbz(A64.X6, "copied");
        a.B("copy");
        a.Label("copied");
        a.AddImm(A64.X1, A64.X1, 1);
        a.StrWord(A64.X1, A64.X0, RingIndexOffset);                    // inc dword [block+RingIndex]
        a.Ldrb(A64.X1, A64.X0, EnabledOffset);
        a.Cbz(A64.X1, "done");
        a.LdrWord(A64.X1, A64.X0, NationCountOffset);
        a.Cbz(A64.X1, "drawn");                                        // no list: every regen
        a.Cbz64(A64.X19, "done");
        a.Ldrb(A64.X2, A64.X0, ScopeOffset);
        a.CmpImm32(A64.X2, (int)GoldenGenerationScope.Club);
        a.BCond(A64.Eq, "club");
        a.CmpImm32(A64.X2, (int)GoldenGenerationScope.Continent);
        a.BCond(A64.Eq, "continent");
        a.Label("scopeNation");                                        // Nation (falls here from the compares)
        a.LdrImm(A64.X3, A64.X19, recordNation);
        a.Cbz64(A64.X3, "nationFromClub");
        a.B("haveId");
        a.Label("nationFromClub");
        a.LdrImm(A64.X3, A64.X19, recordClub);
        a.Cbz64(A64.X3, "done");
        a.LdrImm(A64.X3, A64.X3, clubNation);
        a.B("haveId");
        a.Label("club");
        a.LdrImm(A64.X3, A64.X19, recordClub);
        a.B("haveId");
        a.Label("continent");
        a.LdrImm(A64.X3, A64.X19, recordNation);
        a.Cbz64(A64.X3, "continentFromClub");
        a.B("haveNationForContinent");
        a.Label("continentFromClub");
        a.LdrImm(A64.X3, A64.X19, recordClub);
        a.Cbz64(A64.X3, "done");
        a.LdrImm(A64.X3, A64.X3, clubNation);
        a.Label("haveNationForContinent");
        a.Cbz64(A64.X3, "done");
        a.LdrImm(A64.X3, A64.X3, continent);
        a.Label("haveId");
        a.Cbz64(A64.X3, "done");
        a.LdrWord(A64.X3, A64.X3, 0xC);                                // the object's unique id
        a.AddImm(A64.X4, A64.X0, NationsOffset);
        a.Label("scan");
        a.LdrWord(A64.X5, A64.X4, 0);
        a.CmpReg(A64.X3, A64.X5);
        a.BCond(A64.Eq, "drawn");
        a.AddImm(A64.X4, A64.X4, 4);
        a.SubImm32(A64.X1, A64.X1, 1);
        a.Cbz(A64.X1, "done");
        a.B("scan");
        a.Label("drawn");
        a.Ldrh(A64.X2, A64.X0, RangeOffset);
        a.Cbz(A64.X2, "done");
        a.MrsVirtualCount(A64.X4);
        a.Udiv32(A64.X5, A64.X4, A64.X2);                              // rnd / range
        a.Mul32(A64.X5, A64.X5, A64.X2);
        a.SubReg32(A64.X4, A64.X4, A64.X5);                            // rnd % range
        a.Ldrh(A64.X5, A64.X0, MinPaOffset);
        a.AddReg32(A64.X4, A64.X4, A64.X5);                            // draw = min + rnd % range
        a.CmpImm32(A64.X4, MaxPa);
        a.BCond(A64.Le, "capped");
        a.Movz(A64.X4, MaxPa);
        a.Label("capped");
        a.Ldrsh(A64.X6, A64.X20, ca);
        a.CmpReg(A64.X6, A64.X4);
        a.BCond(A64.Le, "storePa");                                    // CA at or below the draw: keep the draw
        a.MovReg(A64.X4, A64.X6);
        a.Label("storePa");
        a.Strh(A64.X4, A64.X20, pa);
        a.LdrImm(A64.X5, A64.X0, RaisedOffset);
        a.AddImm(A64.X5, A64.X5, 1);
        a.StrImm(A64.X5, A64.X0, RaisedOffset);                        // inc qword [block+Raised]
        a.Ldrb(A64.X5, A64.X0, WonderkidOffset);
        a.Cbz(A64.X5, "done");
        a.MrsVirtualCount(A64.X4);
        a.Movz(A64.X7, 100);
        a.Udiv32(A64.X6, A64.X4, A64.X7);
        a.Mul32(A64.X6, A64.X6, A64.X7);
        a.SubReg32(A64.X4, A64.X4, A64.X6);                            // rnd % 100
        a.CmpReg(A64.X4, A64.X5);
        a.BCond(A64.Hs, "done");                                       // chance missed
        a.Ldrsh(A64.X6, A64.X20, pa);
        a.Ldrsh(A64.X7, A64.X20, ca);
        a.Movz(A64.X4, 70);
        a.Mul32(A64.X4, A64.X6, A64.X4);
        a.Movz(A64.X5, 100);
        a.Udiv32(A64.X4, A64.X4, A64.X5);
        a.CmpReg(A64.X7, A64.X4);
        a.BCond(A64.Ge, "done");                                       // CA at 70% of PA already
        a.Movz(A64.X4, 30);
        a.CmpImm32(A64.X6, 120);
        a.BCond(A64.Lt, "part");
        a.Movz(A64.X4, 40);
        a.CmpImm32(A64.X6, 150);
        a.BCond(A64.Lt, "part");
        a.Movz(A64.X4, 55);
        a.CmpImm32(A64.X6, 180);
        a.BCond(A64.Lt, "part");
        a.Movz(A64.X4, 70);
        a.Label("part");
        a.SubReg32(A64.X5, A64.X6, A64.X7);                            // PA - CA
        a.Mul32(A64.X5, A64.X5, A64.X4);
        a.Movz(A64.X4, 100);
        a.Udiv32(A64.X5, A64.X5, A64.X4);
        a.AddReg32(A64.X5, A64.X5, A64.X7);                            // CA + gap * pct / 100
        a.CmpReg(A64.X5, A64.X6);
        a.BCond(A64.Le, "storeCa");
        a.MovReg(A64.X5, A64.X6);
        a.Label("storeCa");
        a.Strh(A64.X5, A64.X20, ca);
        a.LdrWord(A64.X5, A64.X0, WonderkidCountOffset);
        a.AddImm(A64.X5, A64.X5, 1);
        a.StrWord(A64.X5, A64.X0, WonderkidCountOffset);               // inc dword [block+Wonderkids]
        a.Label("done");
        a.LdrImm(A64.X0, A64.SP, 0x40);                                // restore everything, the flags last
        a.MsrNzcv(A64.X0);
        a.Ldp(A64.X6, A64.X7, A64.SP, 0x30);
        a.Ldp(A64.X4, A64.X5, A64.SP, 0x20);
        a.Ldp(A64.X2, A64.X3, A64.SP, 0x10);
        a.LdpPost(A64.X0, A64.X1, A64.SP, 0x50);
        a.Emit(stolen);                                                // ldrsh w?, [x20+PA]; cmp w?, #0xb3
        a.B(resume);
        return a.Build();
    }

    static void Replace(byte[] body, uint sentinel, uint value, int expected)
    {
        var s = BitConverter.GetBytes(sentinel);
        int found = 0;
        for (int i = 0; i + 4 <= body.Length; i++)
            if (body.AsSpan(i, 4).SequenceEqual(s)) { BitConverter.GetBytes(value).CopyTo(body, i); found++; }
        if (found != expected) throw new InvalidOperationException("The Golden Generation routine template is damaged");
    }

    /// <summary>The routine body from the listing above (Keystone, x64), CA = 0x11223344 and PA = 0x55667788 as placeholders.</summary>
    static readonly byte[] Body =
    [
        0x48, 0xFF, 0x41, 0x08, 0x8B, 0x41, 0x38, 0x83, 0xE0, 0x3F, 0xC1, 0xE0, 0x09, 0x48, 0x8D, 0x94, 0x01, 0x00, 0x04, 0x00,
        0x00, 0x48, 0x89, 0x32, 0x48, 0x89, 0x5A, 0x08, 0x48, 0x89, 0x7A, 0x10, 0x4C, 0x89, 0x62, 0x18, 0x4C, 0x89, 0x6A, 0x20,
        0x4C, 0x89, 0x72, 0x28, 0x4C, 0x89, 0x7A, 0x30, 0x48, 0x8B, 0x47, 0x78, 0x48, 0x89, 0x42, 0x38, 0x4C, 0x8D, 0x44, 0x24,
        0x38, 0x4C, 0x8D, 0x4A, 0x40, 0x41, 0xBA, 0x38, 0x00, 0x00, 0x00, 0x49, 0x8B, 0x00, 0x49, 0x89, 0x01, 0x49, 0x83, 0xC0,
        0x08, 0x49, 0x83, 0xC1, 0x08, 0x41, 0xFF, 0xCA, 0x75, 0xED, 0xFF, 0x41, 0x38, 0x80, 0x79, 0x28, 0x00, 0x0F, 0x84, 0x30,
        0x01, 0x00, 0x00, 0x44, 0x8B, 0x41, 0x30, 0x45, 0x85, 0xC0, 0x74, 0x51, 0x0F, 0xB6, 0x41, 0x2F, 0x3C, 0x01, 0x74, 0x0A,
        0x3C, 0x03, 0x74, 0x0C, 0x48, 0x8B, 0x47, 0x68, 0xEB, 0x1A, 0x48, 0x8B, 0x47, 0x78, 0xEB, 0x14, 0x48, 0x8B, 0x47, 0x68,
        0x48, 0x85, 0xC0, 0x0F, 0x84, 0x02, 0x01, 0x00, 0x00, 0x48, 0x8B, 0x80, 0x66, 0x55, 0x44, 0x33, 0x48, 0x85, 0xC0, 0x0F,
        0x84, 0xF2, 0x00, 0x00, 0x00, 0x8B, 0x40, 0x0C, 0x48, 0x8D, 0x91, 0x00, 0x03, 0x00, 0x00, 0x39, 0x02, 0x74, 0x0E, 0x48,
        0x83, 0xC2, 0x04, 0x41, 0xFF, 0xC8, 0x75, 0xF3, 0xE9, 0xD6, 0x00, 0x00, 0x00, 0x44, 0x0F, 0xB7, 0x41, 0x2C, 0x45, 0x85,
        0xC0, 0x0F, 0x84, 0xC8, 0x00, 0x00, 0x00, 0x0F, 0x31, 0x31, 0xD2, 0x41, 0xF7, 0xF0, 0x66, 0x03, 0x51, 0x2A, 0x66, 0x81,
        0xFA, 0xC8, 0x00, 0x7E, 0x04, 0x66, 0xBA, 0xC8, 0x00, 0x0F, 0xBF, 0x86, 0x44, 0x33, 0x22, 0x11, 0x66, 0x39, 0xC2, 0x7D,
        0x03, 0x66, 0x89, 0xC2, 0x66, 0x89, 0x96, 0x88, 0x77, 0x66, 0x55, 0x48, 0xFF, 0x41, 0x10, 0x44, 0x0F, 0xB6, 0x51, 0x2E,
        0x45, 0x85, 0xD2, 0x0F, 0x84, 0x8A, 0x00, 0x00, 0x00, 0x0F, 0x31, 0x31, 0xD2, 0x41, 0xB8, 0x64, 0x00, 0x00, 0x00, 0x41,
        0xF7, 0xF0, 0x44, 0x39, 0xD2, 0x73, 0x78, 0x44, 0x0F, 0xBF, 0x8E, 0x88, 0x77, 0x66, 0x55, 0x44, 0x0F, 0xBF, 0x96, 0x44,
        0x33, 0x22, 0x11, 0x41, 0x6B, 0xC1, 0x46, 0x31, 0xD2, 0x41, 0xF7, 0xF0, 0x41, 0x39, 0xC2, 0x7D, 0x5A, 0x41, 0xB8, 0x1E,
        0x00, 0x00, 0x00, 0x41, 0x83, 0xF9, 0x78, 0x7C, 0x24, 0x41, 0xB8, 0x28, 0x00, 0x00, 0x00, 0x41, 0x81, 0xF9, 0x96, 0x00,
        0x00, 0x00, 0x7C, 0x15, 0x41, 0xB8, 0x37, 0x00, 0x00, 0x00, 0x41, 0x81, 0xF9, 0xB4, 0x00, 0x00, 0x00, 0x7C, 0x06, 0x41,
        0xB8, 0x46, 0x00, 0x00, 0x00, 0x44, 0x89, 0xC8, 0x44, 0x29, 0xD0, 0x41, 0x0F, 0xAF, 0xC0, 0x31, 0xD2, 0x41, 0xB8, 0x64,
        0x00, 0x00, 0x00, 0x41, 0xF7, 0xF0, 0x44, 0x01, 0xD0, 0x44, 0x39, 0xC8, 0x7E, 0x03, 0x44, 0x89, 0xC8, 0x66, 0x89, 0x86,
        0x44, 0x33, 0x22, 0x11, 0xFF, 0x41, 0x3C,
    ];
}
