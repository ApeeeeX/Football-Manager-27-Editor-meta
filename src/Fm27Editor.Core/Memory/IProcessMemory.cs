namespace Fm27Editor.Core.Memory;

/// <summary>Raw access to another process's address space.</summary>
public interface IProcessMemory
{
    /// <summary>Fills <paramref name="buffer"/> from <paramref name="address"/>; false if any byte was unreadable.</summary>
    bool TryRead(ulong address, Span<byte> buffer);

    bool TryWrite(ulong address, ReadOnlySpan<byte> data);
}

/// <summary>Optional bounded pipelining of independent read-only requests. Each destination must hold 1–32 bytes;
/// <c>succeeded</c> must have exactly one flag per request, including requests for duplicate addresses.</summary>
public interface IScatterReadMemory
{
    void ReadMany(IReadOnlyList<MemoryReadRequest> requests, Span<bool> succeeded);
}

public readonly record struct MemoryReadRequest(ulong Address, Memory<byte> Destination);

/// <summary>Optional native/in-process heap vtable scanner to avoid copying gigabytes over IPC.</summary>
public interface IVtableHeapScanner
{
    IReadOnlyList<HeapVtableMatch>? TryScanVtables(
        IReadOnlyList<MemoryRegion> regions,
        ReadOnlySpan<ulong> vtables,
        bool tolerateUnreadable,
        bool unreadablePagesAreNotHeap,
        CancellationToken ct);
}

/// <summary>A provider that supports simultaneous read-only calls from independent worker threads.</summary>
public interface IConcurrentMemoryReads : IProcessMemory { }

/// <summary>Optional enumeration of writable regions for read-only discovery (heap scans); omissions do not prove absence.</summary>
public interface IMemoryRegions
{
    /// <summary>Committed regions that are writable data: private heap pages and, inside
    /// [imageStart, imageEnd), writable image pages (a module's static data).</summary>
    IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0);
}

/// <summary>Optional capability: a memory that can hand out fresh blocks inside the process.</summary>
public interface IRemoteAllocator
{
    /// <summary>False when the process was opened read-only.</summary>
    bool CanWrite { get; }

    /// <summary>Fresh zeroed read/write memory inside the process.</summary>
    ulong Allocate(int bytes);
}

/// <summary>
/// Optional capability: zeroed memory from the game's own heap (<c>calloc</c> inside the process), for records and
/// vector buffers the game will later <c>free</c>. A block from <see cref="IRemoteAllocator"/> is a raw VM region and
/// makes the game abort when it frees it (macOS: "pointer being freed was not allocated", 19/11/2026).
/// </summary>
public interface IGameHeapAllocator
{
    ulong AllocateFromGameHeap(int bytes);
}

/// <summary>Optional capability: tells a listener about writes and fresh blocks, so an edit history can follow them.</summary>
public interface IMemoryWriteObserver
{
    /// <summary>Called after every successful write with the address, the bytes that were there and the bytes written.</summary>
    Action<ulong, byte[], byte[]>? OnWrite { get; set; }

    /// <summary>Called with the address and size of each fresh block.</summary>
    Action<ulong, int>? OnAllocate { get; set; }
}

public enum AllocationOrigin { Unknown = 0, RawVm = 1, GameHeap = 2 }

public sealed record HeapBlockCheck(bool Passed, string Mode, int? UserTag, ulong? MallocSize, string Detail);

/// <summary>Optional capability distinguishing raw VM blocks from game-allocator (<c>calloc</c>) blocks.</summary>
public interface IMemoryAllocationObserver
{
    Action<ulong, int, AllocationOrigin>? OnAllocateWithOrigin { get; set; }
}

/// <summary>Optional capability checking whether a pointer actually lives on the game's C heap.</summary>
public interface IGameHeapVerifier
{
    HeapBlockCheck VerifyGameHeapBlock(ulong address, int requestedBytes);
}

/// <summary>
/// Optional capability: a hook called before every write; it throws to refuse the write (the session uses it while the
/// game is loading a save or has none loaded). Implementations must call it before taking a lock that the hook's reads
/// would need.
/// </summary>
public interface IWriteGate
{
    Action? BeforeWrite { get; set; }
}

public static class ProcessMemoryExtensions
{
    public static byte[] Read(this IProcessMemory memory, ulong address, int length)
    {
        var buffer = new byte[length];
        if (!memory.TryRead(address, buffer))
            throw new MemoryAccessException($"Cannot read {length} bytes at 0x{address:X}");
        return buffer;
    }

    public static ulong ReadUInt64(this IProcessMemory memory, ulong address)
    {
        Span<byte> b = stackalloc byte[8];
        if (!memory.TryRead(address, b)) throw new MemoryAccessException($"Cannot read pointer at 0x{address:X}");
        return BitConverter.ToUInt64(b);
    }
}

public sealed class MemoryAccessException(string message) : Exception(message);

/// <summary>
/// Memory whose writable regions include mappings that are never object storage and cannot be read. On macOS these are the
/// IOSurface and IOAccelerator buffers of the game's window (seen 23/11/2026, Steam 27.1.0: a CAMetalLayer drawable).
/// </summary>
public interface IUnreadablePagesAreNotHeap;

public readonly record struct MemoryRegion(ulong Start, ulong Size, bool IsModuleData)
{
    /// <summary>OS guard pages are reserved access traps, not readable object storage.</summary>
    public bool IsGuard { get; init; }
}
