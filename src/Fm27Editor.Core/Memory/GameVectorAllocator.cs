using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Memory;

/// <summary>
/// How many bytes a block the game may free has to have so that the game's vector allocator can take it back safely.
/// </summary>
/// <remarks>
/// <para>On macOS the game frees a vector buffer with <c>dealloc(allocator, ptr, size)</c>, not <c>free</c>. With pooling on,
/// a size below the number of pools goes to <c>pool[size]</c>: the block is pushed on that pool's free list and later handed
/// out again as a block of the pool's capacity, which is the size class (1560 bytes go to the 2048-byte class). A
/// <c>calloc(1560)</c> block reused that way gets 2048 bytes written into it and overwrites the next malloc block; that was the
/// Save As and holiday crashes of 22/11/2026 (Steam 27.1.0, a freed object at <c>buffer + 0x700</c> of a grown future-transfer
/// date vector). Larger sizes go to <c>free(ptr - header)</c>.</para>
/// <para>So a block is allocated with the capacity of the pool its size maps to. Both the capacity and the pool's block
/// header are read from the running game each time; a pool with a block header, or an unpooled allocator with a size
/// header, is refused, because a <c>calloc</c> block has neither.</para>
/// </remarks>
public static class GameVectorAllocator
{
    const int MaxPools = 1 << 16;

    /// <summary>The number of bytes to allocate for a block of <paramref name="size"/> bytes.</summary>
    public static int BlockBytes(IProcessMemory memory, VectorAllocatorOffsets offsets, ulong moduleBase, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        ulong allocator = moduleBase + offsets.InstanceRva;
        byte pooled = memory.Read(allocator + (ulong)offsets.Pooled, 1)[0];
        int header = BitConverter.ToInt32(memory.Read(allocator + (ulong)offsets.Header, 4));
        if (header != 0) throw new MemoryAccessException($"The game's vector allocator puts a {header}-byte header in front of a block");
        if (pooled == 0) return size;
        if (pooled != 1) throw new MemoryAccessException($"The game's vector allocator pooling flag reads {pooled}");
        ulong begin = memory.ReadUInt64(allocator + (ulong)offsets.Pools);
        ulong end = memory.ReadUInt64(allocator + (ulong)offsets.Pools + 8);
        if (begin == 0 || end < begin || (end - begin) % 8 != 0 || (end - begin) / 8 > MaxPools)
            throw new MemoryAccessException("The game's vector allocator pool list is not readable");
        ulong count = (end - begin) / 8;
        if ((ulong)size >= count) return size;
        ulong pool = memory.ReadUInt64(begin + (ulong)size * 8);
        if (pool == 0) return size;
        int capacity = BitConverter.ToInt32(memory.Read(pool + (ulong)offsets.PoolCapacity, 4));
        int poolHeader = BitConverter.ToInt32(memory.Read(pool + (ulong)offsets.PoolHeader, 4));
        if (poolHeader != 0) throw new MemoryAccessException($"The game's {size}-byte pool puts a {poolHeader}-byte header in front of a block");
        if (capacity < size || capacity > 1 << 20) throw new MemoryAccessException($"The game's {size}-byte pool reports a capacity of {capacity}");
        return capacity;
    }
}
