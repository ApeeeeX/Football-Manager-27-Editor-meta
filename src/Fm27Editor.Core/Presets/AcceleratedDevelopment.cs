using System.Buffers.Binary;
using Fm27Editor.Core.CodeGen;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Presets;

/// <summary>What the code patch needs from the process besides reads and writes.</summary>
public interface ICodePatchHost
{
    /// <summary>Executable memory close enough to <paramref name="near"/> for a rel32 jump.</summary>
    ulong AllocateNear(ulong near, int bytes);

    /// <summary>Changes page protection and returns the previous value.</summary>
    uint Protect(ulong address, int bytes, uint protect);

    /// <summary>Stops every thread of the process until the result is disposed, so no thread runs half-patched code.</summary>
    IDisposable Pause();

    /// <summary>Makes the processor see freshly written code.</summary>
    void FlushCode(ulong address, int bytes);

    /// <summary>
    /// Makes a block from <see cref="AllocateNear"/> executable after its contents are written. On a W^X host the block
    /// is allocated read/write and must be flipped to read/execute here, once, before the jump is installed; on a host
    /// whose <see cref="AllocateNear"/> already returns executable memory (Windows) this is a no-op.
    /// </summary>
    void CommitExecutable(ulong address, int bytes) { }

    /// <summary>Releases a block from <see cref="AllocateNear"/>; a host that cannot free leaves it to the process.</summary>
    void Free(ulong address) { }
}

/// <summary>One player whose development is sped up, as the game process holds it.</summary>
/// <summary>One player whose development is sped up; <see cref="Multiplier"/> in steps of 0.1 (2.5 = two and a half times).</summary>
public sealed record AcceleratedPlayer(ulong Address, int PlayerId, double Multiplier);

/// <summary>Whether the patch is in place, how often it has run, and who it speeds up.</summary>
public sealed record AcceleratedState(ulong Site, ulong Cave, long Calls, long Hits, bool LiftZeroGains, IReadOnlyList<AcceleratedPlayer> Players)
{
    /// <summary>The last objects (r15) the game passed through the site, newest first; for checking the hook sees players.</summary>
    public IReadOnlyList<ulong> Recent { get; init; } = [];

    /// <summary>The block was written by an earlier editor version (whole-number multipliers); Install replaces it.</summary>
    public bool IsOlderVersion { get; init; }
}

/// <summary>
/// "Accelerate Player Development": the game adds a player's training gain to his Current Ability in one place
/// (<c>movzx eax, word [r15+CA]</c>, <c>add ax, di</c>, then CA is capped at PA). A jump there leads to a small routine
/// in the game process that looks the player (<c>r15</c>) up in a table and multiplies the gain (<c>di</c>) by that
/// player's multiplier. The game's own cap keeps CA at or below PA.
/// </summary>
/// <remarks>
/// <para>Idea from the Cheat Engine table preset of the same name (FM26). The site is found by its bytes, built from the
/// profile's CA and PA offsets, so no address is fixed for one build; exactly one match is required. The routine is
/// written here from scratch and filters only by the table: choosing players is done in the editor.</para>
/// <para>Table changes are ordinary writes into the routine's block, so the edit history can undo them. Installing and
/// removing the jump pause the game for the length of one write.</para>
/// </remarks>
public sealed class AcceleratedDevelopment(IProcessMemory memory, ICodePatchHost? host, OffsetProfile profile, ulong moduleBase, int moduleSize)
{
    /// <summary>Speeds are 1.1x to 20x in steps of 0.1; 1x (or 0) means normal speed.</summary>
    public const double MinMultiplier = 1.1, MaxMultiplier = 20;

    /// <summary>The largest gain one call can produce after multiplying; CA is 1–200, so this never wraps.</summary>
    public const int MaxGain = 200;

    public static readonly double[] OfferedMultipliers = [2, 3, 5, 10, 20];

    /// <summary>True for a speed the routine can hold: 1.1-20 with at most one decimal.</summary>
    public static bool IsAllowedMultiplier(double value) =>
        value >= MinMultiplier - 1e-9 && value <= MaxMultiplier + 1e-9 && Math.Abs(value * 10 - Math.Round(value * 10)) < 1e-6;

    // Block layout. Header fields are read and written by the editor; the routine only counts and reads.
    internal const int MagicOffset = 0x00, CallsOffset = 0x08, HitsOffset = 0x10, OriginalOffset = 0x18, SiteOffset = 0x20,
        CountOffset = 0x28, LiftZeroOffset = 0x30, RingIndexOffset = 0x38, CodeOffset = 0x40, RingOffset = 0x180, RingSize = 16,
        TableOffset = 0x200, EntrySize = 0x10;

    /// <summary>
    /// The mutable part of the block: magic, counters, ring index, ring and table. It is never executed, so on a host
    /// that enforces W^X it stays read/write for the whole life of the patch — the routine writes the counters, the ring
    /// and the table on every pass. On x86-64 the routine shares the block exactly as before; ARM64 puts it on its own
    /// page (<see cref="CodePage"/>) so only that page has to be made read/execute.
    /// </summary>
    const int DataSize = 0x10000;

    /// <summary>Where the ARM64 routine starts: the second 64 KB granule, i.e. a page of its own.</summary>
    const int CodePage = 0x10000;

    /// <summary>The smallest page an ARM64 host can protect (16 KB on Apple Silicon), also the ARM64 code slot.</summary>
    const int Arm64PageSize = 0x4000;

    /// <summary>Where the routine starts, per instruction set.</summary>
    static int CodeBaseOf(bool arm64) => arm64 ? CodePage : CodeOffset;

    /// <summary>How much memory the block needs: the data region, plus a code page on ARM64.</summary>
    static int BlockBytesOf(bool arm64) => arm64 ? CodePage + Arm64PageSize : BlockSize;

    ulong CodeBase => (ulong)CodeBaseOf(profile.IsArm64);
    int BlockBytes => BlockBytesOf(profile.IsArm64);

    /// <summary>How many bytes the routine may use before it runs into the data (x86-64) or the end of the block (ARM64).</summary>
    int CodeSlot => profile.IsArm64 ? Arm64PageSize : RingOffset - CodeOffset;
    public const int BlockSize = 0x10000;
    public const int Capacity = (DataSize - TableOffset) / EntrySize;
    /// <summary>Version 2 keeps multipliers in tenths; version 1 (an earlier editor) kept whole numbers.</summary>
    internal static readonly byte[] Magic = "FM27ACC2"u8.ToArray(), MagicV1 = "FM27ACC1"u8.ToArray();
    const int StolenLengthX64 = 8, StolenLengthArm64 = 8;
    const uint PageExecuteReadWrite = 0x40;

    ulong? site;

    PlayerFieldOffsets Fields => profile.Fields;

    /// <summary>Bytes the jump replaces: half an instruction pair on x86-64 is fine, ARM64 steals whole instructions.</summary>
    int StolenLength => profile.IsArm64 ? StolenLengthArm64 : StolenLengthX64;

    /// <summary>
    /// Bytes at the site before patching: <c>movzx eax, word [r15+CA]; add ax, di; movzx ecx, word [r15+PA]</c>.
    /// </summary>
    public static byte[] OriginalPattern(OffsetProfile profile)
    {
        var p = new List<byte> { 0x41, 0x0F, 0xB7, 0x87 };
        p.AddRange(BitConverter.GetBytes(profile.Fields.CurrentAbility));
        p.AddRange([0x66, 0x01, 0xF8, 0x41, 0x0F, 0xB7, 0x8F]);
        p.AddRange(BitConverter.GetBytes(profile.Fields.PotentialAbility));
        return [.. p];
    }

    /// <summary>
    /// The ARM64 site: <c>ldrh w8, [x23+CA]; add w8, w8, w22; sxth w8, w8; ldrsh w9, [x23+PA]</c>. The gain is in
    /// <c>w22</c> (the game computes it with <c>sub w22, w22, w8</c> just before) and the player is <c>x23</c>, so the
    /// cave scales <c>w22</c> before the game's own add and cap run. No register number is pinned, because the compiler
    /// renumbers them between builds (the two known builds read the player through <c>x23</c> and <c>x19</c> and take the
    /// gain in <c>w22</c> and <c>w24</c>); <see cref="SameRegisters"/> requires the words to agree instead, and the
    /// routine takes both registers from the stolen bytes. The PA load is what tells this CA update from the three
    /// others in a build. Field offsets come from the profile.
    /// </summary>
    Arm64Word[] Arm64Pattern() =>
    [
        new(Arm64Load, AnyRegisters),              // ldrh Rt, [Base, #CA]
        new(0x0B000000, AnyRegisters),             // add Rd, Rn, Rm
        new(0x13003C00, AnyRegisters),             // sxth Rd, Rn
        new(Arm64PotentialLoad, AnyRegisters),     // ldrsh Rt, [Base, #PA]
    ];

    /// <summary>Everything but the three register fields (Rd, Rn, Rm), so a rebuild may renumber them all.</summary>
    const uint AnyRegisters = 0xFFE0_FC00;

    /// <summary><c>ldrh w8, [x23, #CA]</c>; the register fields are covered by the mask and filled in again below.</summary>
    uint Arm64Load => 0x79400000u | ((uint)(Fields.CurrentAbility / 2) << 10) | (23u << 5) | 8;

    /// <summary><c>ldrsh w9, [x23, #PA]</c>, the cap instruction that follows the site and names the same player.</summary>
    uint Arm64PotentialLoad => 0x79C00000u | ((uint)(Fields.PotentialAbility / 2) << 10) | (23u << 5) | 9;

    /// <summary>
    /// True when the four site words agree on the registers: the load and the add both write the register the sxth
    /// sign-extends, the load and the ldrsh read through the same base, and the gain comes in through a third register.
    /// That is what separates the site from the other CA update in the build (a call-return path doing
    /// <c>ldrh w9; add w8, w9, w8</c>, where the add does not write the register it reads).
    /// </summary>
    static bool SameRegisters(scoped ReadOnlySpan<uint> words)
    {
        uint load = words[0], add = words[1], sxth = words[2], ldrsh = words[3];
        uint baseRegister = (load >> 5) & 0x1F, written = load & 0x1F, gain = (add >> 16) & 0x1F;
        return (ldrsh >> 5 & 0x1F) == baseRegister      // the cap load reads the same player
            && (add & 0x1F) == written                  // the add writes where the load wrote
            && (add >> 5 & 0x1F) == written             // ... and adds to the value it just read
            && (sxth & 0x1F) == written                 // the cap sign-extends that same value
            && gain != written;                         // the gain arrives from another register
    }

    /// <summary>The bytes the site must hold before patching, in the profile's instruction set.</summary>
    byte[] OriginalBytes() => profile.IsArm64
        ? Arm64Pattern().SelectMany(w => BitConverter.GetBytes(w.Value)).ToArray()
        : OriginalPattern(profile).AsSpan(0, StolenLength).ToArray();

    /// <summary>
    /// True when the site still holds the code the patch replaces: whole bytes on x86-64, and on ARM64 the masked words
    /// (a register number may legitimately differ from the one the pattern names) with the register agreement checked.
    /// </summary>
    bool SiteHolds(ulong at)
    {
        if (!profile.IsArm64) return memory.Read(at, StolenLength).AsSpan().SequenceEqual(OriginalBytes());
        var pattern = Arm64Pattern();
        var bytes = memory.Read(at, pattern.Length * 4).AsSpan();
        var values = new uint[pattern.Length];
        for (int i = 0; i < pattern.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i * 4)..]);
            if (!pattern[i].Matches(values[i])) return false;
        }
        return SameRegisters(values);
    }

    /// <summary>
    /// The check a patched site cannot do from the site itself: its first two words are the jump and a NOP, so they are
    /// read back from the block the jump points at, once the block's magic and site field say the block is this hook's
    /// own. A jump into something else is accepted as it is, which at worst leaves the site ambiguous and refused.
    /// </summary>
    bool Accept(uint[] words, ulong at)
    {
        if (!Arm64IsBranchWord(words[0])) return SameRegisters(words);
        ulong routine = at + (ulong)((long)((int)(words[0] << 6) >> 6) * 4);
        var head = new byte[Math.Max(SiteOffset + sizeof(ulong), OriginalOffset + StolenLengthArm64)];
        if (!memory.TryRead(routine - CodeBase, head)) return true;
        if (!head.AsSpan(MagicOffset, Magic.Length).SequenceEqual(Magic)
            && !head.AsSpan(MagicOffset, MagicV1.Length).SequenceEqual(MagicV1)) return true;
        if (BitConverter.ToUInt64(head, SiteOffset) != at) return true;
        // A block from the one-word era has no add word to compare, so its magic alone has to do.
        return SameRegisters([BitConverter.ToUInt32(head, OriginalOffset),
            BitConverter.ToUInt32(head, OriginalOffset + 4), words[2], words[3]]);
    }

    /// <summary>True when the site already carries our jump (an x86-64 <c>E9</c>, or an ARM64 <c>B</c>).</summary>
    bool IsPatched(ReadOnlySpan<byte> at) =>
        profile.IsArm64 ? Arm64IsBranch(at) : at[0] == 0xE9;

    static bool Arm64IsBranch(ReadOnlySpan<byte> at) =>
        at.Length >= 4 && Arm64IsBranchWord(BinaryPrimitives.ReadUInt32LittleEndian(at));

    /// <summary>True for an ARM64 <c>B</c> word (opcode 000101, imm26).</summary>
    static bool Arm64IsBranchWord(uint word) => (word & 0xFC000000) == 0x14000000;

    /// <summary>The routine address a jump at <paramref name="site"/> points to.</summary>
    ulong RoutineAddress(ulong site, ReadOnlySpan<byte> at) => profile.IsArm64
        ? site + (ulong)((long)((int)(BinaryPrimitives.ReadUInt32LittleEndian(at) << 6) >> 6) * 4)
        : site + 5 + (ulong)(long)BitConverter.ToInt32(at[1..]);

    /// <summary>The same bytes once patched: the jump, then the stolen instructions' remainder untouched.</summary>
    bool IsPatchedSite(ReadOnlySpan<byte> at, byte[] original) =>
        at.Length >= original.Length && IsPatched(at) && (profile.IsArm64 || (at[5] == 0x90 && at[6] == 0x90 && at[7] == 0x90))
        && at[StolenLength..original.Length].SequenceEqual(original.AsSpan(StolenLength));

    /// <summary>The site: the one place in the module's code holding the training-gain code, patched or not.</summary>
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
                    if (span[i] != 0x41 && span[i] != 0xE9) continue;
                    var here = span[i..];
                    if (here[..original.Length].SequenceEqual(original) || IsPatchedSite(here, original))
                        hits.Add(start + offset + (ulong)i);
                }
                if (hits.Count > 1) break;
            }
            if (hits.Count > 1) break;
        }
        if (hits.Count == 0) throw new InvalidOperationException($"{Feature}: the training-gain code was not found in this build");
        if (hits.Count > 1) throw new InvalidOperationException($"{Feature}: the training-gain code is not unique in this build");
        return (site = hits[0]).Value;
    }

    /// <summary>The name the site-finding errors use.</summary>
    const string Feature = "Accelerate Player Development";

    /// <summary>Executable sections of the loaded module, from its PE header.</summary>
    IEnumerable<(ulong Start, ulong Size)> CodeSections()
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

    /// <summary>The current state, or null when the patch is not installed.</summary>
    public AcceleratedState? Read()
    {
        ulong at = FindSite();
        var bytes = memory.Read(at, StolenLength);
        if (!IsPatched(bytes)) return null;
        ulong code = RoutineAddress(at, bytes);
        ulong cave = code - CodeBase;
        var head = memory.Read(cave, TableOffset);
        bool v1 = head.AsSpan(MagicOffset, MagicV1.Length).SequenceEqual(MagicV1);
        if (!v1 && !head.AsSpan(MagicOffset, Magic.Length).SequenceEqual(Magic) || BitConverter.ToUInt64(head, SiteOffset) != at)
            throw new InvalidOperationException("The training-gain code is patched by another tool");
        int count = BitConverter.ToInt32(head, CountOffset);
        if (count is < 0 or > Capacity) throw new InvalidOperationException("The accelerated player table is damaged");
        var players = new List<AcceleratedPlayer>(count);
        if (count > 0)
        {
            var rows = memory.Read(cave + TableOffset, count * EntrySize);
            for (int i = 0; i < count; i++)
                players.Add(new(BitConverter.ToUInt64(rows, i * EntrySize), BitConverter.ToInt32(rows, i * EntrySize + 12),
                    BitConverter.ToUInt16(rows, i * EntrySize + 8) / (v1 ? 1.0 : 10.0)));
        }
        uint ring = BitConverter.ToUInt32(head, RingIndexOffset);
        var recent = new List<ulong>(RingSize);
        for (int i = 1; i <= Math.Min(RingSize, (int)Math.Min(ring, RingSize)); i++)
            recent.Add(BitConverter.ToUInt64(head, RingOffset + (int)((ring - (uint)i) % RingSize) * 8));
        return new(at, cave, BitConverter.ToInt64(head, CallsOffset), BitConverter.ToInt64(head, HitsOffset), head[LiftZeroOffset] != 0, players)
            { Recent = recent, IsOlderVersion = v1 };
    }

    /// <summary>Puts the jump and the routine in place (once per game run); returns the state.</summary>
    public AcceleratedState Install(bool liftZeroGains = true)
    {
        if (Read() is { } installed)
            return RoutineIsCurrent(installed) || host is null ? installed : Migrate(installed);
        if (host is null) throw new InvalidOperationException("Accelerate Player Development needs a writable game process");
        ulong at = FindSite();
        var original = memory.Read(at, StolenLength);
        if (!SiteHolds(at))
            throw new StaleObjectException("The training-gain code changed while it was being read");

        ulong cave = host.AllocateNear(at, BlockBytes);
        var block = BuildBlockFor(cave, at, original, liftZeroGains);
        Put(cave, block);
        host.FlushCode(cave, block.Length);
        CommitRoutine(cave);

        var patch = PatchBytes(at, cave + CodeBase);
        using (host.Pause())
        {
            if (!memory.Read(at, StolenLength).AsSpan().SequenceEqual(original))
                throw new StaleObjectException("The training-gain code changed before the patch");
            uint previous = host.Protect(at, StolenLength, PageExecuteReadWrite);
            try { Put(at, patch); }
            finally { host.Protect(at, StolenLength, previous); }
            host.FlushCode(at, StolenLength);
        }
        return Read() ?? throw new MemoryAccessException("The patch did not stay in place");
    }

    bool RoutineIsCurrent(AcceleratedState state)
    {
        if (state.IsOlderVersion) return false;
        var stolen = memory.Read(state.Cave + OriginalOffset, StolenLength);
        var expected = BuildRoutineFor(state.Cave + CodeBase, state.Cave, state.Site + (ulong)StolenLength, stolen);
        return memory.Read(state.Cave + CodeBase, expected.Length).AsSpan().SequenceEqual(expected);
    }

    /// <summary>
    /// The game holds an older routine (an earlier editor version wrote it): a new block gets the current routine, the
    /// counters and the table, then the jump is pointed at it. The old block is left alone, since a paused thread may be
    /// inside it; it finishes there and returns to the game as before.
    /// </summary>
    AcceleratedState Migrate(AcceleratedState old)
    {
        var stolen = memory.Read(old.Cave + OriginalOffset, StolenLength);
        ulong cave = host!.AllocateNear(old.Site, BlockBytes);
        var block = BuildBlockFor(cave, old.Site, stolen, old.LiftZeroGains);
        BitConverter.GetBytes(old.Calls).CopyTo(block, CallsOffset);
        BitConverter.GetBytes(old.Hits).CopyTo(block, HitsOffset);
        BitConverter.GetBytes(old.Players.Count).CopyTo(block, CountOffset);
        for (int i = 0; i < old.Players.Count; i++)
            EntryBytes(old.Players[i].Address, old.Players[i].PlayerId, old.Players[i].Multiplier).CopyTo(block, TableOffset + i * EntrySize);  // Read already turned v1 whole numbers into multipliers
        Put(cave, block);
        host.FlushCode(cave, block.Length);
        CommitRoutine(cave);
        var patch = PatchBytes(old.Site, cave + CodeBase);
        using (host.Pause())
        {
            var current = memory.Read(old.Site, StolenLength);
            if (!IsPatched(current) || RoutineAddress(old.Site, current) != old.Cave + CodeBase)
                throw new StaleObjectException("The training-gain patch changed while it was being updated");
            uint previous = host.Protect(old.Site, StolenLength, PageExecuteReadWrite);
            try { Put(old.Site, patch); }
            finally { host.Protect(old.Site, StolenLength, previous); }
            host.FlushCode(old.Site, StolenLength);
        }
        return Read() ?? throw new MemoryAccessException("The patch did not stay in place");
    }

    /// <summary>Puts the game's original bytes back. The routine's block stays allocated: a paused thread may still be in it.</summary>
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
    /// Sets the player's speed (<see cref="MinMultiplier"/>-<see cref="MaxMultiplier"/> in steps of 0.1), or takes him out of
    /// the table with 0 or 1 (normal speed). Returns false when nothing changed. Installs the patch on first use.
    /// </summary>
    public bool Set(PlayerRecord player, double multiplier)
    {
        if (multiplier == 1) multiplier = 0;
        if (multiplier != 0 && !IsAllowedMultiplier(multiplier))
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, $"Speed must be {MinMultiplier}-{MaxMultiplier} with at most one decimal, or 1 for normal");
        if (player.Id is not > 0) throw new InvalidOperationException($"{player.DisplayName}: the player has no readable id");
        RequirePlayer(player);
        var state = multiplier == 0 ? Read() : Install();
        if (state is null) return false;
        int index = state.Players.ToList().FindIndex(p => p.Address == player.Address);
        if (multiplier == 0)
        {
            if (index < 0) return false;
            int last = state.Players.Count - 1;
            if (index != last) PutBlock(Entry(state.Cave, index), memory.Read(Entry(state.Cave, last), EntrySize));
            PutBlock(state.Cave + CountOffset, BitConverter.GetBytes(last));
            return true;
        }
        if (index >= 0)
        {
            if (Math.Abs(state.Players[index].Multiplier - multiplier) < 1e-9 && state.Players[index].PlayerId == player.Id) return false;
            PutBlock(Entry(state.Cave, index), EntryBytes(player.Address, player.Id.Value, multiplier));
            return true;
        }
        if (state.Players.Count >= Capacity) throw new InvalidOperationException($"At most {Capacity} players can be accelerated at once");
        // The row goes in before the count grows, so the routine never sees a half-written row.
        PutBlock(Entry(state.Cave, state.Players.Count), EntryBytes(player.Address, player.Id.Value, multiplier));
        PutBlock(state.Cave + CountOffset, BitConverter.GetBytes(state.Players.Count + 1));
        return true;
    }

    /// <summary>The player's multiplier, or 0 when he is not accelerated (or the patch is not installed).</summary>
    public double MultiplierOf(PlayerRecord player) =>
        Read()?.Players.FirstOrDefault(p => p.Address == player.Address)?.Multiplier ?? 0;

    /// <summary>Takes out every row whose object is no longer that player (another save was loaded into the same game run).</summary>
    public int RemoveStale()
    {
        if (Read() is not { } state) return 0;
        int removed = 0;
        foreach (var row in state.Players.Reverse())
        {
            if (StillThePlayer(row)) continue;
            var fresh = Read()!;
            int index = fresh.Players.ToList().FindIndex(p => p.Address == row.Address);
            int last = fresh.Players.Count - 1;
            if (index != last) PutBlock(Entry(fresh.Cave, index), memory.Read(Entry(fresh.Cave, last), EntrySize));
            PutBlock(fresh.Cave + CountOffset, BitConverter.GetBytes(last));
            removed++;
        }
        return removed;
    }

    bool StillThePlayer(AcceleratedPlayer row)
    {
        if (Fields.Id is not { } id) return false;
        var b = new byte[8];
        if (!memory.TryRead(row.Address, b)) return false;
        ulong vtable = BitConverter.ToUInt64(b);
        if (!profile.PlayerVtableRvas.Any(r => vtable == moduleBase + r)) return false;
        return memory.TryRead(row.Address + (ulong)id, b.AsSpan(0, 4)) && BitConverter.ToInt32(b, 0) == row.PlayerId;
    }

    void RequirePlayer(PlayerRecord player)
    {
        if (!StillThePlayer(new(player.Address, player.Id ?? 0, 0)))
            throw new StaleObjectException($"{player.DisplayName}: the player object changed since it was read");
    }

    static ulong Entry(ulong cave, int index) => cave + TableOffset + (ulong)(index * EntrySize);

    static byte[] EntryBytes(ulong address, int playerId, double multiplier)
    {
        var row = new byte[EntrySize];
        BitConverter.GetBytes(address).CopyTo(row, 0);
        BitConverter.GetBytes((ushort)Math.Round(multiplier * 10)).CopyTo(row, 8);   // tenths
        BitConverter.GetBytes(playerId).CopyTo(row, 12);
        return row;
    }

    /// <summary>
    /// Makes the routine's slot executable. On ARM64 that is the whole code page (the block reserves one, so the
    /// protect call is page-aligned); on x86-64 the host ignores it and the slot stops at the ring.
    /// </summary>
    void CommitRoutine(ulong cave) => host!.CommitExecutable(cave + CodeBase, CodeSlot);

    void Put(ulong address, byte[] bytes)
    {
        if (!memory.TryWrite(address, bytes) || !memory.Read(address, bytes.Length).AsSpan().SequenceEqual(bytes))
            throw new MemoryAccessException($"Could not write 0x{address:X}");
    }

    /// <summary>
    /// Writes into the routine's block, which is read/execute once installed (W^X hosts have to be told to let the
    /// page be written again, and told to make it executable again afterwards). The switches happen around the one
    /// write, so no thread can run code that is briefly writable.
    /// </summary>
    void PutBlock(ulong address, byte[] bytes)
    {
        if (host is null) { Put(address, bytes); return; }
        uint previous = host.Protect(address, bytes.Length, PageExecuteReadWrite);
        try { Put(address, bytes); }
        finally { host.Protect(address, bytes.Length, previous); }
        host.FlushCode(address, bytes.Length);
    }

    /// <summary>The jump that replaces the stolen bytes: <c>jmp rel32</c> and three nops, or one ARM64 <c>B</c>.</summary>
    public static byte[] BuildPatch(ulong from, ulong to) => BuildPatch(from, to, arm64: false);

    /// <summary>The jump for the profile's instruction set.</summary>
    byte[] PatchBytes(ulong from, ulong to) => BuildPatch(from, to, profile.IsArm64);

    static byte[] BuildPatch(ulong from, ulong to, bool arm64) => arm64
        ? Arm64CodePatch.BuildPatch(from, to, StolenLengthArm64)
        : X64Patch(from, to);

    static byte[] X64Patch(ulong from, ulong to)
    {
        long delta = (long)to - (long)(from + 5);
        if (delta is < int.MinValue or > int.MaxValue) throw new InvalidOperationException("The routine is out of reach of a rel32 jump");
        var patch = new byte[StolenLengthX64];
        patch[0] = 0xE9;
        BitConverter.GetBytes((int)delta).CopyTo(patch, 1);
        patch[5] = patch[6] = patch[7] = 0x90;
        return patch;
    }

    /// <summary>
    /// The whole block: header, routine and an empty table. The routine, in order:
    /// <code>
    /// pushfq; push rax; push rcx; push rdx
    /// lea rcx,[block]; inc qword [rcx+Calls]
    /// mov edx,[rcx+Count]; lea rax,[rcx+Table]
    /// next: test edx,edx; jz done; cmp [rax],r15; je hit; add rax,16; dec edx; jmp next
    /// hit:  test di,di; jg grow; cmp byte [rcx+LiftZero],0; je done; mov di,1
    /// grow: edx = word [rax+8] (multiplier in tenths); edx = (di * edx + 5) / 10; at most MaxGain
    /// keep: mov di,dx; inc qword [rcx+Hits]
    /// done: pop rdx; pop rcx; pop rax; popfq
    ///       (the eight stolen bytes: movzx eax, word [r15+CA])
    ///       jmp back to the instruction after them
    /// </code>
    /// A gain below one is only lifted to one when <paramref name="liftZeroGains"/> is set (the Cheat Engine preset
    /// always does); a negative gain (decline) is lifted the same way, never multiplied.
    /// </summary>
    public static byte[] BuildBlock(ulong cave, ulong site, byte[] stolen, bool liftZeroGains) =>
        BuildBlock(cave, site, stolen, liftZeroGains, arm64: false);

    /// <summary>The block for the profile's instruction set.</summary>
    public byte[] BuildBlockFor(ulong cave, ulong site, byte[] stolen, bool liftZeroGains) =>
        BuildBlock(cave, site, stolen, liftZeroGains, profile.IsArm64);

    static byte[] BuildBlock(ulong cave, ulong site, byte[] stolen, bool liftZeroGains, bool arm64)
    {
        int stolenLength = arm64 ? StolenLengthArm64 : StolenLengthX64;
        if (stolen.Length != stolenLength)
            throw new ArgumentException($"{stolenLength} stolen bytes are expected", nameof(stolen));
        int codeBase = CodeBaseOf(arm64);
        int slot = arm64 ? Arm64PageSize : RingOffset - CodeOffset;
        var block = new byte[BlockBytesOf(arm64)];
        Magic.CopyTo(block, MagicOffset);
        stolen.CopyTo(block, OriginalOffset);
        BitConverter.GetBytes(site).CopyTo(block, SiteOffset);
        block[LiftZeroOffset] = liftZeroGains ? (byte)1 : (byte)0;
        var code = arm64
            ? BuildRoutineArm64(cave + (ulong)codeBase, cave, site + StolenLengthArm64, stolen)
            : BuildRoutine(cave + (ulong)codeBase, cave, site + StolenLengthX64, stolen);
        if (code.Length > slot)
            throw new InvalidOperationException($"The routine does not fit its slot ({code.Length} > {slot} bytes)");
        code.CopyTo(block, codeBase);
        return block;
    }

    byte[] BuildRoutineFor(ulong at, ulong cave, ulong resume, byte[] stolen) =>
        profile.IsArm64 ? BuildRoutineArm64(at, cave, resume, stolen) : BuildRoutine(at, cave, resume, stolen);

    internal static byte[] BuildRoutine(ulong at, ulong cave, ulong resume, byte[] stolen)
    {
        var a = new MiniAssembler(at);
        a.Emit(0x9C, 0x50, 0x51, 0x52);                                   // pushfq; push rax; push rcx; push rdx
        a.LeaRipRcx(cave);                                                // lea rcx,[rip+cave]
        a.Emit(0x48, 0xFF, 0x41, CallsOffset);                            // inc qword [rcx+Calls]
        a.Emit(0x8B, 0x41, RingIndexOffset);                              // mov eax,[rcx+RingIndex]
        a.Emit(0x83, 0xE0, RingSize - 1);                                 // and eax,15
        a.Emit(0x4C, 0x89, 0xBC, 0xC1); a.Int32(RingOffset);              // mov [rcx+rax*8+Ring],r15
        a.Emit(0xFF, 0x41, RingIndexOffset);                              // inc dword [rcx+RingIndex]
        a.Emit(0x8B, 0x51, CountOffset);                                  // mov edx,[rcx+Count]
        a.Emit(0x48, 0x8D, 0x81); a.Int32(TableOffset);                   // lea rax,[rcx+Table]
        a.Label("next");
        a.Emit(0x85, 0xD2); a.Jump8(0x74, "done");                        // test edx,edx; jz done
        a.Emit(0x4C, 0x39, 0x38); a.Jump8(0x74, "hit");                   // cmp [rax],r15; je hit
        a.Emit(0x48, 0x83, 0xC0, EntrySize);                              // add rax,16
        a.Emit(0xFF, 0xCA); a.Jump8(0xEB, "next");                        // dec edx; jmp next
        a.Label("hit");
        a.Emit(0x66, 0x85, 0xFF); a.Jump8(0x7F, "grow");                  // test di,di; jg grow
        a.Emit(0x80, 0x79, LiftZeroOffset, 0x00); a.Jump8(0x74, "done");  // cmp byte [rcx+LiftZero],0; je done
        a.Emit(0x66, 0xBF, 0x01, 0x00);                                   // mov di,1
        a.Label("grow");
        a.Emit(0x0F, 0xB7, 0x50, 0x08);                                   // movzx edx,word [rax+8]   (tenths)
        a.Emit(0x0F, 0xBF, 0xC7);                                         // movsx eax,di             (gain > 0 here)
        a.Emit(0x0F, 0xAF, 0xC2);                                         // imul eax,edx
        a.Emit(0x83, 0xC0, 0x05);                                         // add eax,5                (round)
        a.Emit(0xBA); a.Int32(unchecked((int)0xCCCCCCCD));                // mov edx,0xCCCCCCCD
        a.Emit(0xF7, 0xE2);                                               // mul edx
        a.Emit(0xC1, 0xEA, 0x03);                                         // shr edx,3                (edx = eax / 10)
        a.Emit(0x81, 0xFA); a.Int32(MaxGain); a.Jump8(0x7E, "keep");      // cmp edx,MaxGain; jle keep
        a.Emit(0xBA); a.Int32(MaxGain);                                   // mov edx,MaxGain
        a.Label("keep");
        a.Emit(0x66, 0x89, 0xD7);                                         // mov di,dx
        a.Emit(0x48, 0xFF, 0x41, HitsOffset);                             // inc qword [rcx+Hits]
        a.Label("done");
        a.Emit(0x5A, 0x59, 0x58, 0x9D);                                   // pop rdx; pop rcx; pop rax; popfq
        a.Emit(stolen);                                                   // movzx eax, word [r15+CA]
        a.Jump32(resume);                                                 // jmp back
        return a.Finish();
    }

    /// <summary>
    /// The ARM64 routine, the counterpart of <see cref="BuildRoutine"/>. The site loads CA through a base register and
    /// adds the training gain the game is about to apply; this scales the gain instead of the x86-64 <c>di</c>. It saves
    /// x0-x9 and the flags, counts, rings the player, looks him up, scales, caps at <see cref="MaxGain"/>, then re-runs
    /// the two stolen instructions and branches back.
    /// </summary>
    /// <remarks>
    /// The player object is read through the base register of the stolen load and the gain lives in the add's <c>Rm</c>;
    /// neither is pinned, because the compiler put them in <c>x23</c>/<c>w22</c> on the first build and
    /// <c>x19</c>/<c>w24</c> on the second. Both are copied out of the stolen bytes into x7 (player) and x8 (gain,
    /// scaled), so the body never depends on where the game keeps them, and the scaled gain goes back into its own
    /// register after the restores. Everything the routine touches is on the stack before that and back afterwards, so
    /// the replay and the game see every register as it was. The cave base is reached with <c>ADRP</c>/<c>ADD</c>
    /// (position independent), and the way back is a <c>B</c>, since the host allocates the cave within branch reach.
    /// </remarks>
    /// <remarks>
    /// The player object is read through the base register of the stolen load and the gain lives in the add's third
    /// register; neither is pinned, because the compiler puts them in different registers on different builds. Both are
    /// taken from the stolen bytes and copied into x7 (player) and x8 (gain, scaled), so the body never depends on where
    /// the game keeps them. Everything the routine touches, x0-x9 and the flags, is on the stack before that and back
    /// afterwards; the scaled gain is written into its own register after the restores and the stolen instructions then
    /// run again with every register the way the game left it.
    /// </remarks>
    internal static byte[] BuildRoutineArm64(ulong at, ulong cave, ulong resume, byte[] stolen)
    {
        int player = (int)((BinaryPrimitives.ReadUInt32LittleEndian(stolen) >> 5) & 0x1F);
        int gain = (int)((BinaryPrimitives.ReadUInt32LittleEndian(stolen.AsSpan(4)) >> 16) & 0x1F);
        var a = new Arm64Assembler(at);
        a.StpPre(A64.X0, A64.X1, A64.SP, -0x60);                          // save the scratch registers and the flags
        a.Stp(A64.X2, A64.X3, A64.SP, 0x10);
        a.Stp(A64.X4, A64.X5, A64.SP, 0x20);
        a.Stp(A64.X6, A64.X7, A64.SP, 0x30);
        a.Stp(A64.X8, A64.X9, A64.SP, 0x40);
        a.MrsNzcv(A64.X0);
        a.StrImm(A64.X0, A64.SP, 0x50);
        MoveRegFrom(a, A64.X7, player);                                   // x7 = the player object
        a.MovReg(A64.X8, gain);
        a.StrWord(A64.X8, A64.SP, GainSlot);                              // w8 = the gain, which stays in the slot as well
        a.Adrp(A64.X9, cave);                                             // x9 = [block]
        a.AddImm(A64.X9, A64.X9, (int)(cave & 0xFFF));
        a.LdrImm(A64.X0, A64.X9, CallsOffset);
        a.AddImm(A64.X0, A64.X0, 1);
        a.StrImm(A64.X0, A64.X9, CallsOffset);                            // inc qword [block+Calls]
        a.LdrWord(A64.X1, A64.X9, RingIndexOffset);
        a.AndLowBits(A64.X1, A64.X1, 4);                                  // RingSize is 16
        a.AddImm(A64.X2, A64.X9, RingOffset);
        a.StrRegScaled8(A64.X7, A64.X2, A64.X1);                          // ring[RingIndex & 15] = player
        a.AddImm(A64.X1, A64.X1, 1);
        a.StrWord(A64.X1, A64.X9, RingIndexOffset);                       // inc dword [block+RingIndex]
        a.LdrWord(A64.X1, A64.X9, CountOffset);                           // w1 = count
        a.AddImm(A64.X2, A64.X9, TableOffset);                            // x2 = &table
        a.Label("next");
        a.Cbz(A64.X1, "done");                                            // nothing left to look at
        a.LdrImm(A64.X3, A64.X2, 0);                                      // x3 = entry address
        a.CmpReg(A64.X3, A64.X7);
        a.BCond(A64.Eq, "hit");
        a.AddImm(A64.X2, A64.X2, EntrySize);
        a.SubImm32(A64.X1, A64.X1, 1);
        a.B("next");
        a.Label("hit");
        a.Cbz(A64.X8, "maybeLift");                                       // no gain yet: consider lifting one
        a.B("grow");
        a.Label("maybeLift");
        a.Ldrb(A64.X0, A64.X9, LiftZeroOffset);
        a.Cbz(A64.X0, "done");                                            // nothing to lift
        a.Movz(A64.X8, 1);                                                // a zero (or negative) gain becomes 1
        a.Label("grow");
        a.Ldrh(A64.X0, A64.X2, 8);                                        // multiplier in tenths
        a.MovReg(A64.X3, A64.X8);
        a.Mul32(A64.X3, A64.X3, A64.X0);
        a.AddImm32(A64.X3, A64.X3, 5);                                    // round
        a.Movz(A64.X4, 10);
        a.Udiv32(A64.X3, A64.X3, A64.X4);                                 // /10
        a.CmpImm32(A64.X3, MaxGain);
        a.BCond(A64.Le, "keep");
        a.Movz(A64.X3, MaxGain);
        a.Label("keep");
        a.MovReg(A64.X8, A64.X3);
        a.StrWord(A64.X8, A64.SP, GainSlot);
        a.LdrImm(A64.X0, A64.X9, HitsOffset);
        a.AddImm(A64.X0, A64.X0, 1);
        a.StrImm(A64.X0, A64.X9, HitsOffset);                             // inc qword [block+Hits]
        a.Label("done");
        a.LdrImm(A64.X0, A64.SP, 0x50);
        a.MsrNzcv(A64.X0);
        a.Ldp(A64.X0, A64.X1, A64.SP, 0x00);
        a.Ldp(A64.X2, A64.X3, A64.SP, 0x10);
        a.Ldp(A64.X4, A64.X5, A64.SP, 0x20);
        a.Ldp(A64.X6, A64.X7, A64.SP, 0x30);
        a.Ldp(A64.X8, A64.X9, A64.SP, 0x40);
        if (gain == A64.SP)                                               // a gain register of SP needs a hop
        {
            a.LdrWord(A64.X8, A64.SP, GainSlot);
            a.AddImm(A64.SP, A64.SP, 0x60);
            a.AddImm(A64.SP, A64.X8, 0);
        }
        else
        {
            a.LdrWord(gain, A64.SP, GainSlot);                            // the gain, scaled or not, goes back
            a.AddImm(A64.SP, A64.SP, 0x60);
        }
        a.Emit(stolen);                                                   // ldrh Rt, [Base, CA]; add Rd, Rn, Rm
        a.B(resume);
        return a.Build();
    }

    /// <summary>Where the routine keeps the gain it is about to put back: the last word of its frame.</summary>
    const int GainSlot = 0x58;

    /// <summary><c>ADD rd, rm, #0</c>: a move that also accepts the stack pointer, which the register move cannot encode.</summary>
    static void MoveRegFrom(Arm64Assembler a, int rd, int rm) => a.AddImm(rd, rm, 0);

    /// <summary>Just enough of an x64 assembler for the routine above: raw bytes, rel8 jumps to labels, rel32 jumps out.</summary>
    sealed class MiniAssembler(ulong origin)
    {
        readonly List<byte> code = [];
        readonly Dictionary<string, int> labels = [];
        readonly List<(int At, string Label)> fixups = [];

        public void Emit(params int[] bytes) { foreach (int b in bytes) code.Add(checked((byte)b)); }
        public void Emit(byte[] bytes) => code.AddRange(bytes);
        public void Int32(int value) => code.AddRange(BitConverter.GetBytes(value));
        public void Int16(int value) => code.AddRange(BitConverter.GetBytes((short)value));
        public void Label(string name) => labels[name] = code.Count;

        public void Jump8(byte opcode, string label)
        {
            code.Add(opcode);
            fixups.Add((code.Count, label));
            code.Add(0);
        }

        public void LeaRipRcx(ulong target)
        {
            code.AddRange([0x48, 0x8D, 0x0D]);
            long delta = (long)target - (long)(origin + (ulong)code.Count + 4);
            code.AddRange(BitConverter.GetBytes(checked((int)delta)));
        }

        public void Jump32(ulong target)
        {
            code.Add(0xE9);
            long delta = (long)target - (long)(origin + (ulong)code.Count + 4);
            code.AddRange(BitConverter.GetBytes(checked((int)delta)));
        }

        public byte[] Finish()
        {
            foreach (var (at, label) in fixups)
            {
                int delta = labels[label] - (at + 1);
                code[at] = unchecked((byte)checked((sbyte)delta));
            }
            return [.. code];
        }
    }
}
