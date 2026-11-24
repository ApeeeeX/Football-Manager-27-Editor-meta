using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

public sealed class TransferDealScanTests
{
    const ulong Module = 0x7FF000000000, Start = 0x10000000;
    const int Chunk = 1 << 22;
    static OffsetProfile Profile(string build) => OffsetProfile.LoadFile(Path.Combine(AppContext.BaseDirectory, "offsets", build + ".json"));

    sealed class MacRegions : Regions, IUnreadablePagesAreNotHeap;

    [Fact]
    public void Mac_scan_skips_an_unreadable_page_and_keeps_later_objects()
    {
        var memory = new MacRegions { RejectWide = true, RejectPage = true };
        BitConverter.GetBytes(0x11111UL).CopyTo(memory.Bytes, 0);
        BitConverter.GetBytes(0x11111UL).CopyTo(memory.Bytes, 8192);
        Assert.Equal([Start, Start + 8192], HeapVtableScanner.Scan(memory, 0x11111).Select(m => m.Address));
    }

    class Regions : IProcessMemory, IMemoryRegions
    {
        public byte[] Bytes = new byte[Chunk + 19];
        public bool RejectWide, RejectPage;
        public int Enumerations; public MemoryRegion[]? Source;
        public bool TryRead(ulong address, Span<byte> bytes)
        {
            if (RejectWide && bytes.Length > 4096 || RejectPage && address == Start + 4096) return false;
            if (address < Start || address + (ulong)bytes.Length > Start + (ulong)Bytes.Length) return false;
            Bytes.AsSpan((int)(address - Start), bytes.Length).CopyTo(bytes);
            return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => throw new Exception("Read-only scan wrote memory");
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        {
            Enumerations++; if (Source is not null) { foreach (var r in Source) yield return r; yield break; }
            yield return new(Start, (ulong)Bytes.Length, false);
            yield return new MemoryRegion(0x90000000, 4096, false) { IsGuard = true }; // unreadable access trap
            yield return new(Start, (ulong)Bytes.Length, true); // must not duplicate module data
        }
        public void Word(int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(Bytes, offset);
    }

    sealed class ConcurrentRegions : Regions, IConcurrentMemoryReads { }

    [Theory]
    [InlineData("27.0.0+2493426-gamepass", 0x4EE4098UL, false)]
    [InlineData("27.0.0+2493426-steam", 0x4F3B2E8UL, false)]
    [InlineData("27.0.0+2493426-gamepass", 0x4EE4098UL, true)]
    [InlineData("27.0.0+2493426-steam", 0x4F3B2E8UL, true)]
    public void Scan_preserves_order_variants_and_chunk_tails(string build, ulong profileVtable, bool concurrent)
    {
        var memory = concurrent ? new ConcurrentRegions() : new Regions();
        // Explicit fixture addresses, not values computed by the scanner.
        memory.Word(0, Module + profileVtable);
        memory.Word(Chunk - 8, Module + 74494248);
        memory.Word(Chunk, Module + 83071720);
        memory.Word(Chunk + 8, Module + profileVtable);
        var reader = new TransferDealReader(memory, Profile(build), Module);
        Assert.Equal(new ulong[] { Start, Start + Chunk - 8, Start + Chunk, Start + Chunk + 8 }, reader.Scan());
    }

    [Fact]
    public void Shared_scan_keeps_each_type_and_emits_a_duplicate_target_only_once()
    {
        var memory = new ConcurrentRegions();
        memory.Word(0, 0x11111);
        memory.Word(8, 0x22222);
        memory.Word(16, 0x33333);
        memory.Word(24, 0x44444);
        memory.Word(32, 0x55555);
        Assert.Equal(new[] {
            new HeapVtableMatch(Start, 0x11111), new HeapVtableMatch(Start + 8, 0x22222),
            new HeapVtableMatch(Start + 16, 0x33333), new HeapVtableMatch(Start + 24, 0x44444)
        }, HeapVtableScanner.Scan(memory, 0x11111, 0x22222, 0x33333, 0x44444, 0x11111));
    }
    [Fact]
    public void Concurrent_regions_preserve_enumeration_order_instead_of_address_order()
    {
        var memory = new ConcurrentRegions { Source = [new(Start + 16, 8, false), new(Start, 8, false)] };
        memory.Word(0, Module + 74494248);
        memory.Word(16, Module + 83071720);
        Assert.Equal(new ulong[] { Start + 16, Start }, new TransferDealReader(memory, Profile("27.0.0+2493426-gamepass"), Module).Scan());
    }
    [Fact]
    public void Failed_speculative_read_retries_pages_without_losing_candidates()
    {
        var memory = new Regions { RejectWide = true };
        memory.Word(4096, Module + 74494248);
        memory.Word(Chunk + 8, Module + 83071720);
        Assert.Equal(new ulong[] { Start + 4096, Start + Chunk + 8 }, new TransferDealReader(memory, Profile("27.0.0+2493426-gamepass"), Module).Scan());
    }

    [Fact]
    public void Unreadable_page_is_an_error_not_partial_or_empty_success()
    {
        var memory = new Regions { RejectWide = true, RejectPage = true };
        memory.Word(0, Module + 74494248);
        Assert.Throws<MemoryAccessException>(() => new TransferDealReader(memory, Profile("27.0.0+2493426-gamepass"), Module).Scan());
    }

    [Fact]
    public void Retained_candidates_read_fee_and_status_live_without_discovery()
    {
        var memory = new Regions();
        memory.Word(0, Module + 74494248);
        memory.Word(0x38, 0x30000);
        var reader = new TransferDealReader(memory, Profile("27.0.0+2493426-gamepass"), Module);
        var candidates = reader.Scan();
        BitConverter.GetBytes(1000000).CopyTo(memory.Bytes, 0x58);
        BitConverter.GetBytes(0xFF00).CopyTo(memory.Bytes, 0x88);
        Assert.Equal(1000000, reader.FindForPlayer(0x30000, candidates)!.Fee);
        BitConverter.GetBytes(2000000).CopyTo(memory.Bytes, 0x58);
        BitConverter.GetBytes(0xFF02).CopyTo(memory.Bytes, 0x88);
        var changed = reader.FindForPlayer(0x30000, candidates)!;
        Assert.Equal(2000000, changed.Fee);
        Assert.Equal(0xFF02, changed.StatusRaw);
        memory.Word(0, 0);
        Assert.Null(reader.FindForPlayer(0x30000, candidates));
        Assert.Equal(1, memory.Enumerations);
    }

    [Theory]
    [InlineData("27.1.0+2505117-steam", false)]
    [InlineData("27.1.0+2505117-gamepass", false)]
    [InlineData("27.1.0+2505117-gamepass", true)]
    public void Owned_transfer_deals_require_a_verified_slot_for_empty_or_sparse_copies(string build, bool sparse)
    {
        var profile = Profile(build);
        var p = profile.Proposals!;
        Assert.Equal(0x38, p.CopyVector);
        Assert.Equal(0x38, OffsetProfile.Load(profile.ToJson()).Proposals!.CopyVector);

        var mem = new FakeMemory();
        ulong mgr = 0x10_0000, vec = 0x18_0000, offer = 0x20_0000;
        var rootSlot = mem.Map(Module + p.ManagerRva!.Value, 8);
        BitConverter.GetBytes(mgr).CopyTo(rootSlot, 0);

        var mgrBytes = mem.Map(mgr, Math.Max(p.OfferSerial + 4, 0x800));
        BitConverter.GetBytes(Module + p.ManagerVtable!.Value).CopyTo(mgrBytes, 0);
        BitConverter.GetBytes(vec).CopyTo(mgrBytes, p.OfferVector);
        BitConverter.GetBytes(vec + 8).CopyTo(mgrBytes, p.OfferVector + 8);
        BitConverter.GetBytes(vec + 8).CopyTo(mgrBytes, p.OfferVector + 16);

        var vecBytes = mem.Map(vec, 8);
        BitConverter.GetBytes(offer).CopyTo(vecBytes, 0);

        var offerBytes = mem.Map(offer, 0x100);
        BitConverter.GetBytes(Module + p.TransferVtable).CopyTo(offerBytes, 0);
        BitConverter.GetBytes(0).CopyTo(offerBytes, p.Id);

        ulong copy = 0x30_0000;
        if (sparse)
        {
            // A Game Pass save had one offer copy followed by two null slots in the copy vector.
            const ulong copyVector = 0x17_0000;
            const ulong person = 0x40_0000;
            BitConverter.GetBytes(copyVector).CopyTo(mgrBytes, 0x38);
            BitConverter.GetBytes(copyVector + 24).CopyTo(mgrBytes, 0x40);
            BitConverter.GetBytes(copyVector + 24).CopyTo(mgrBytes, 0x48);
            BitConverter.GetBytes(copy).CopyTo(mem.Map(copyVector, 24), 0);
            BitConverter.GetBytes(person).CopyTo(offerBytes, 0x38);
            offerBytes.CopyTo(mem.Map(copy, offerBytes.Length), 0);
        }

        ulong[] expected = sparse ? [offer, copy] : [offer];
        Assert.Equal(expected, new TransferDealReader(mem, profile, Module).TryReadOwnedCandidates());

        var doc = System.Text.Json.Nodes.JsonNode.Parse(profile.ToJson())!;
        doc["player"]!["proposals"]!["copyVector"] = null;
        var unverified = OffsetProfile.Load(doc.ToJsonString());
        Assert.Null(new TransferDealReader(mem, unverified, Module).TryReadOwnedCandidates());
    }
}
