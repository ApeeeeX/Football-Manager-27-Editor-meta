using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;
using Xunit;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The allocator layout and the values below were read from Steam 27.1.0 macOS on 22/11/2026 (poolscan): 4097 pools,
/// pooling on, no size header, every pool's block header 0, and the 1560-byte pool's capacity 0x800.
/// </summary>
public class GameVectorAllocatorTests
{
    const ulong Module = 0x1_0000_0000, Pools = 0x2_0000_0000, Pool1560 = 0x3_0000_0000, Pool16 = 0x3_0001_0000;
    static readonly VectorAllocatorOffsets Offsets = new(0x5791750, 0x18, 0x34, 0x30, 0x144, 0x148);

    static FakeMemory Game(byte pooled = 1, int header = 0, int poolHeader = 0, int poolCount = 4097)
    {
        var memory = new FakeMemory();
        var allocator = memory.Map(Module + Offsets.InstanceRva, 0x40);
        BitConverter.GetBytes(Pools).CopyTo(allocator, 0x18);
        BitConverter.GetBytes(Pools + (ulong)poolCount * 8).CopyTo(allocator, 0x20);
        BitConverter.GetBytes(header).CopyTo(allocator, 0x30);
        allocator[0x34] = pooled;
        var pools = memory.Map(Pools, poolCount * 8);
        if (poolCount > 1560) BitConverter.GetBytes(Pool1560).CopyTo(pools, 1560 * 8);
        BitConverter.GetBytes(Pool16).CopyTo(pools, 16 * 8);
        var p1560 = memory.Map(Pool1560, 0x150);
        BitConverter.GetBytes(0x800).CopyTo(p1560, 0x140);
        BitConverter.GetBytes(0x800).CopyTo(p1560, 0x144);
        BitConverter.GetBytes(poolHeader).CopyTo(p1560, 0x148);
        var p16 = memory.Map(Pool16, 0x150);
        BitConverter.GetBytes(16).CopyTo(p16, 0x144);
        return memory;
    }

    [Fact]
    public void Pooled_size_gets_the_pool_capacity()
        => Assert.Equal(0x800, GameVectorAllocator.BlockBytes(Game(), Offsets, Module, 1560));

    [Fact]
    public void Exact_class_size_stays_the_same()
        => Assert.Equal(16, GameVectorAllocator.BlockBytes(Game(), Offsets, Module, 16));

    [Fact]
    public void Size_past_the_pools_is_left_to_free()
        => Assert.Equal(71264, GameVectorAllocator.BlockBytes(Game(), Offsets, Module, 71264));

    [Fact]
    public void Unpooled_allocator_keeps_the_size()
        => Assert.Equal(1560, GameVectorAllocator.BlockBytes(Game(pooled: 0), Offsets, Module, 1560));

    [Fact]
    public void Pool_with_a_block_header_is_refused()
        => Assert.Throws<MemoryAccessException>(() => GameVectorAllocator.BlockBytes(Game(poolHeader: 16), Offsets, Module, 1560));

    [Fact]
    public void Allocator_with_a_size_header_is_refused()
        => Assert.Throws<MemoryAccessException>(() => GameVectorAllocator.BlockBytes(Game(header: 16), Offsets, Module, 1560));

    [Theory]
    [InlineData(0, 0x800)]
    [InlineData(16, null)]
    public void Windows_msvc_pool_layout_rounds_up_and_rejects_pool_headers(int poolHeader, int? expectedBytes)
    {
        // Windows 27.1.0 Steam/Game Pass MSVC pool struct places poolCapacity at +0x74 and poolHeader at +0x78.
        var winOffsets = new VectorAllocatorOffsets(0x5878D48, 0x18, 0x34, 0x30, 0x74, 0x78);
        var memory = new FakeMemory();
        var allocator = memory.Map(Module + winOffsets.InstanceRva, 0x40);
        BitConverter.GetBytes(Pools).CopyTo(allocator, 0x18);
        BitConverter.GetBytes(Pools + 4097UL * 8).CopyTo(allocator, 0x20);
        BitConverter.GetBytes(0).CopyTo(allocator, 0x30);
        allocator[0x34] = 1;
        var pools = memory.Map(Pools, 4097 * 8);
        BitConverter.GetBytes(Pool1560).CopyTo(pools, 1560 * 8);
        var p1560 = memory.Map(Pool1560, 0x80);
        BitConverter.GetBytes(0x800).CopyTo(p1560, 0x70);
        BitConverter.GetBytes(0x800).CopyTo(p1560, 0x74);
        BitConverter.GetBytes(poolHeader).CopyTo(p1560, 0x78);

        if (expectedBytes is { } expected)
            Assert.Equal(expected, GameVectorAllocator.BlockBytes(memory, winOffsets, Module, 1560));
        else
            Assert.Throws<MemoryAccessException>(() => GameVectorAllocator.BlockBytes(memory, winOffsets, Module, 1560));
    }
}
