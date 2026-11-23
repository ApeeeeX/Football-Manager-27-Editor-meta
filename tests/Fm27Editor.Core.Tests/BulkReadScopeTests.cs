using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class BulkReadScopeTests
{
    [Fact]
    public void Nearby_fields_share_one_process_read_and_preserve_the_bytes()
    {
        var raw = new FakeMemory();
        var page = raw.Map(0x1000, 4096);
        page[3] = 17;
        page[4000] = 29;
        var probe = new Probe(raw);
        using var reads = new BulkReadScope(probe);

        Assert.Equal(new byte[] { 17 }, reads.Memory.Read(0x1003, 1));
        Assert.Equal(new byte[] { 29 }, reads.Memory.Read(0x1FA0, 1));
        Assert.Equal(new[] { (0x1000UL, 4096) }, probe.Requests);
        Assert.Equal(0, raw.Writes);
    }

    [Fact]
    public void Page_crossing_read_falls_back_to_the_entire_requested_range()
    {
        var raw = new FakeMemory();
        var allocation = raw.Map(0x1000, 4104);
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }.CopyTo(allocation, 4092);
        var probe = new Probe(raw)
        {
            BeforeRead = (address, _) => { if (address == 0x2000) allocation[4092] = 19; },
        };
        using var reads = new BulkReadScope(probe);

        Assert.Equal(new byte[] { 19, 2, 3, 4, 5, 6, 7, 8 }, reads.Memory.Read(0x1FFC, 8));
        Assert.Equal(new[] { (0x1000UL, 4096), (0x2000UL, 4096), (0x1FFCUL, 8) }, probe.Requests);
    }

    [Fact]
    public void Failed_read_ahead_does_not_hide_readable_fields_or_cache_a_failure()
    {
        var raw = new FakeMemory();
        raw.Map(0x1010, 4)[0] = 7;
        var probe = new Probe(raw);
        using var reads = new BulkReadScope(probe);
        Assert.Equal(new byte[] { 7, 0, 0, 0 }, reads.Memory.Read(0x1010, 4));
        Assert.False(reads.Memory.TryRead(0x1020, new byte[1]));

        raw.Map(0x1000, 4096)[0x20] = 23;
        Assert.Equal(new byte[] { 23 }, reads.Memory.Read(0x1020, 1));
        Assert.Equal(3, probe.Requests.Count(r => r == (0x1000UL, 4096)));
    }

    [Fact]
    public void Transport_exceptions_remain_visible()
    {
        var probe = new Probe(new FakeMemory()) { BeforeRead = (_, _) => throw new IOException("disconnected") };
        using var reads = new BulkReadScope(probe);
        Assert.Throws<IOException>(() => reads.Memory.Read(0x1000, 1));
        Assert.Single(probe.Requests);
    }

    [Fact]
    public void Failed_page_hint_still_retries_exact_bytes_when_they_become_readable_and_is_bounded()
    {
        var raw = new FakeMemory();
        var probe = new Probe(raw);
        using var reads = new BulkReadScope(probe, pageCount: 1, failedPageHintCount: 1);
        Assert.False(reads.Memory.TryRead(0x1010, new byte[1]));
        raw.Map(0x1010, 1)[0] = 47;
        Assert.Equal(new byte[] { 47 }, reads.Memory.Read(0x1010, 1));
        Assert.Equal(new[] { (0x1000UL, 4096), (0x1010UL, 1), (0x1010UL, 1) }, probe.Requests);

        Assert.False(reads.Memory.TryRead(0x2010, new byte[1]));
        Assert.Equal(new byte[] { 47 }, reads.Memory.Read(0x1010, 1));
        Assert.Equal(2, probe.Requests.Count(r => r == (0x1000UL, 4096)));
    }

    [Fact]
    public void Cache_is_bounded_and_evicted_fields_are_read_again()
    {
        var raw = new FakeMemory();
        var data = raw.Map(0x1000, 3 * 4096);
        data[0] = 1; data[4096] = 2; data[8192] = 3;
        var probe = new Probe(raw);
        using var reads = new BulkReadScope(probe, pageCount: 2);

        Assert.Equal(new byte[] { 1 }, reads.Memory.Read(0x1000, 1));
        Assert.Equal(new byte[] { 2 }, reads.Memory.Read(0x2000, 1));
        Assert.Equal(new byte[] { 3 }, reads.Memory.Read(0x3000, 1));
        Assert.Equal(8192, ((ReadAheadMemory)reads.Memory).CachedBytes);
        data[0] = 4;
        Assert.Equal(new byte[] { 4 }, reads.Memory.Read(0x1000, 1));
        Assert.Equal(8192, ((ReadAheadMemory)reads.Memory).CachedBytes);
        Assert.Equal(4, probe.Requests.Count);
    }

    [Fact]
    public void Nested_disposal_preserves_the_owner_and_outer_disposal_releases_pages_only()
    {
        var raw = new FakeMemory();
        raw.Map(0x1000, 4096)[0] = 31;
        var outer = new BulkReadScope(raw);
        var inner = new BulkReadScope(outer.Memory);
        Assert.Same(outer.Memory, inner.Memory);
        inner.Dispose(); inner.Dispose();
        Assert.Equal(new byte[] { 31 }, outer.Memory.Read(0x1000, 1));
        outer.Dispose(); outer.Dispose();

        Assert.Equal(0, ((ReadAheadMemory)outer.Memory).CachedBytes);
        Assert.Throws<ObjectDisposedException>(() => outer.Memory.Read(0x1000, 1));
        Assert.Equal(new byte[] { 31 }, raw.Read(0x1000, 1));
    }

    [Fact]
    public void New_scope_observes_changed_live_bytes_and_cached_memory_refuses_writes()
    {
        var raw = new FakeMemory();
        var page = raw.Map(0x1000, 4096);
        page[0] = 41;
        using (var first = new BulkReadScope(raw))
        {
            Assert.Equal(new byte[] { 41 }, first.Memory.Read(0x1000, 1));
            Assert.Throws<NotSupportedException>(() => first.Memory.TryWrite(0x1000, new byte[] { 99 }));
            Assert.Equal(0, raw.Writes);
            Assert.True(raw.TryWrite(0x1000, new byte[] { 42 }));
            Assert.Equal(new byte[] { 42 }, raw.Read(0x1000, 1));
        }
        using var next = new BulkReadScope(raw);
        Assert.Equal(new byte[] { 42 }, next.Memory.Read(0x1000, 1));
    }

    [Fact]
    public void Large_reads_bypass_the_cache_and_do_not_evict_small_fields()
    {
        var raw = new FakeMemory();
        raw.Map(0x1000, 4096)[0] = 5;
        raw.Map(0x4000, 8192)[8191] = 6;
        var probe = new Probe(raw);
        using var reads = new BulkReadScope(probe, pageCount: 1);

        Assert.Equal(new byte[] { 5 }, reads.Memory.Read(0x1000, 1));
        Assert.Equal(6, reads.Memory.Read(0x4000, 8192)[8191]);
        Assert.Equal(new byte[] { 5 }, reads.Memory.Read(0x1000, 1));
        Assert.Equal(new[] { (0x1000UL, 4096), (0x4000UL, 8192) }, probe.Requests);
    }

    [Fact]
    public void Bulk_player_load_keeps_reload_and_guarded_writes_on_live_memory()
    {
        var world = new TestWorld();
        var repository = new PlayerRepository(world.Memory, world.Profile, TestWorld.ModuleBase);
        var player = repository.LoadAll().Single(p => p.Address == TestWorld.Mainoo);
        Assert.Equal(153, player.CurrentAbility);
        // Independently measured test layout: CA is int16 at +0x264.
        Assert.True(world.Memory.TryWrite(TestWorld.Mainoo + 0x264, BitConverter.GetBytes((short)154)));
        Assert.Equal(154, repository.Reload(TestWorld.Mainoo)!.CurrentAbility);
        Assert.Equal(154, repository.LoadAll().Single(p => p.Address == TestWorld.Mainoo).CurrentAbility);
        int writes = world.Memory.Writes;
        var writer = new PlayerWriter(world.Memory, world.Profile, TestWorld.ModuleBase);
        Assert.Throws<StaleObjectException>(() => writer.SetCurrentAbility(player, 155));
        Assert.Equal(writes, world.Memory.Writes);
    }

    [Fact]
    public void Prefetch_merges_contiguous_pages_and_tolerates_unreadable_gaps()
    {
        var raw = new FakeMemory();
        var contig = raw.Map(0x1000, 4 * 4096);
        contig[4] = 11;
        contig[4096 + 16] = 22;
        contig[2 * 4096 + 32] = 33;
        contig[3 * 4096 + 48] = 44;
        raw.Map(0x8000, 4096)[8] = 88;
        var probe = new Probe(raw);

        using var reads = new BulkReadScope(probe);
        Assert.Equal(4096, ReadAheadMemory.PageSize);

        reads.Prefetch([0x1004, 0x2010, 0x3020, 0x4030, 0x6000, 0x8008], 32);

        Assert.Equal(new byte[] { 11 }, reads.Memory.Read(0x1004, 1));
        Assert.Equal(new byte[] { 22 }, reads.Memory.Read(0x2010, 1));
        Assert.Equal(new byte[] { 33 }, reads.Memory.Read(0x3020, 1));
        Assert.Equal(new byte[] { 44 }, reads.Memory.Read(0x4030, 1));
        Assert.Equal(new byte[] { 88 }, reads.Memory.Read(0x8008, 1));
        Assert.False(reads.Memory.TryRead(0x6000, new byte[1]));

        Assert.Contains((0x1000UL, 4 * 4096), probe.Requests);
        Assert.DoesNotContain((0x2000UL, 4096), probe.Requests);
        Assert.DoesNotContain((0x3000UL, 4096), probe.Requests);
        Assert.DoesNotContain((0x4000UL, 4096), probe.Requests);
    }

    [Fact]
    public void Prefetch_bridges_small_gaps_without_caching_gap_pages_and_falls_back_on_unreadable_gaps()
    {
        var raw = new FakeMemory();
        var blk1 = raw.Map(0x1_0000, 6 * 4096);
        blk1[8] = 11;
        blk1[3 * 4096 + 8] = 33;
        blk1[5 * 4096 + 8] = 55;
        var blk2a = raw.Map(0x2_0000, 2 * 4096);
        blk2a[0] = 71;
        blk2a[4096] = 72;
        var blk2b = raw.Map(0x2_4000, 2 * 4096);
        blk2b[0] = 73;
        blk2b[4096] = 74;

        var probe = new Probe(raw);
        using (var reads = new BulkReadScope(probe))
        {
            reads.Prefetch([0x1_0000, 0x1_3000, 0x1_5000], 32);
            Assert.Contains((0x1_0000UL, 6 * 4096), probe.Requests);
            Assert.Equal(3 * 4096, ((ReadAheadMemory)reads.Memory).CachedBytes);
            int before = probe.Requests.Count;
            Assert.Equal(new byte[] { 11 }, reads.Memory.Read(0x1_0008, 1));
            Assert.Equal(new byte[] { 33 }, reads.Memory.Read(0x1_3008, 1));
            Assert.Equal(new byte[] { 55 }, reads.Memory.Read(0x1_5008, 1));
            Assert.Equal(before, probe.Requests.Count);
        }

        probe.Requests.Clear();
        using (var reads2 = new BulkReadScope(probe))
        {
            reads2.Prefetch([0x2_0000, 0x2_1000, 0x2_4000, 0x2_5000], 32);
            Assert.Contains((0x2_0000UL, 2 * 4096), probe.Requests);
            Assert.Contains((0x2_4000UL, 2 * 4096), probe.Requests);
            int before = probe.Requests.Count;
            Assert.Equal(new byte[] { 71 }, reads2.Memory.Read(0x2_0000, 1));
            Assert.Equal(new byte[] { 72 }, reads2.Memory.Read(0x2_1000, 1));
            Assert.Equal(new byte[] { 73 }, reads2.Memory.Read(0x2_4000, 1));
            Assert.Equal(new byte[] { 74 }, reads2.Memory.Read(0x2_5000, 1));
            Assert.Equal(before, probe.Requests.Count);
        }
    }

    sealed class Probe(IProcessMemory source) : IProcessMemory
    {
        public List<(ulong Address, int Length)> Requests { get; } = [];
        public Action<ulong, int>? BeforeRead { get; init; }
        public bool TryRead(ulong address, Span<byte> buffer)
        {
            Requests.Add((address, buffer.Length));
            BeforeRead?.Invoke(address, buffer.Length);
            return source.TryRead(address, buffer);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => source.TryWrite(address, data);
    }
}
