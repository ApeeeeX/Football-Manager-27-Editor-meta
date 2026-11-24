using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace Fm27Editor.Core.Memory;

public readonly record struct HeapVtableMatch(ulong Address, ulong Vtable);

/// <summary>One bounded, read-only heap pass for multiple object types. Results preserve region and word order.</summary>
public static class HeapVtableScanner
{
    const int ChunkBytes = 1 << 22;
    public static IReadOnlyList<HeapVtableMatch> Scan(IProcessMemory memory, params ulong[] vtables) =>
        ScanCore(memory, vtables, tolerateUnreadable: false, CancellationToken.None);

    /// <summary>Discovers candidates in a live heap where regions can disappear while being read.</summary>
    internal static IReadOnlyList<HeapVtableMatch> ScanReadable(IProcessMemory memory, ulong vtable, CancellationToken ct) =>
        ScanCore(memory, [vtable], tolerateUnreadable: true, ct);

    /// <summary>Scans heap regions up to <paramref name="maxSmallBytes"/> first, only falling back to medium regions up to <paramref name="maxFallbackBytes"/> on zero hits.</summary>
    internal static IReadOnlyList<HeapVtableMatch> ScanReadableSmallFirst(
        IProcessMemory memory, ulong vtable, ulong maxSmallBytes, ulong maxFallbackBytes, CancellationToken ct)
    {
        if (memory is IVtableHeapScanner)
            return ScanReadable(memory, vtable, ct);
        if (memory is not IMemoryRegions regions)
            throw new NotSupportedException("Heap discovery requires memory region enumeration");
        var writable = regions.EnumerateWritableRegions().Where(r => !r.IsModuleData && !r.IsGuard).ToArray();
        var small = writable.Where(r => r.Size <= maxSmallBytes).ToArray();
        var hits = ScanRegions(memory, small, [vtable], tolerateUnreadable: true, ct);
        if (hits.Count > 0) return hits;
        var medium = writable.Where(r => r.Size > maxSmallBytes && r.Size <= maxFallbackBytes).ToArray();
        return medium.Length > 0 ? ScanRegions(memory, medium, [vtable], tolerateUnreadable: true, ct) : [];
    }

    static IReadOnlyList<HeapVtableMatch> ScanCore(IProcessMemory memory, ulong[] vtables, bool tolerateUnreadable, CancellationToken ct)
    {
        if (memory is not IMemoryRegions regions)
            throw new NotSupportedException("Heap discovery requires memory region enumeration");
        var readable = regions.EnumerateWritableRegions().Where(r => !r.IsModuleData && !r.IsGuard).ToArray();
        if (memory is IVtableHeapScanner nativeScanner &&
            nativeScanner.TryScanVtables(readable, vtables, tolerateUnreadable, memory is IUnreadablePagesAreNotHeap, ct) is { } fastMatches)
            return fastMatches;
        return ScanRegions(memory, readable, vtables, tolerateUnreadable, ct);
    }

    static IReadOnlyList<HeapVtableMatch> ScanRegions(
        IProcessMemory memory, MemoryRegion[] readable, ulong[] vtables, bool tolerateUnreadable, CancellationToken ct)
    {
        if (readable.Length == 0 || vtables.Length == 0) return [];
        var vectors = vtables.Select(v => new Vector<ulong>(v)).ToArray();
        ulong minVt = vtables.Min(), vtSpan = vtables.Max() - minVt;
        var items = new List<(int RegionIndex, ulong Start, int Length, bool IsRegionStart)>(readable.Length);
        for (int ri = 0; ri < readable.Length; ri++)
        {
            var region = readable[ri];
            for (ulong offset = 0; offset + 8 <= region.Size; offset += ChunkBytes)
                items.Add((ri, region.Start + offset, (int)Math.Min(ChunkBytes, region.Size - offset), offset == 0));
        }
        if (items.Count == 0) return [];

        var results = new IReadOnlyList<HeapVtableMatch>[items.Count];
        var errors = new Exception?[items.Count];
        var skipRegion = new bool[readable.Length];
        int next = -1;
        void Worker()
        {
            var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
            try
            {
                int index;
                while (!ct.IsCancellationRequested && (index = Interlocked.Increment(ref next)) < items.Count)
                {
                    var item = items[index];
                    if (Volatile.Read(ref skipRegion[item.RegionIndex])) { results[index] = []; continue; }
                    try { results[index] = ScanChunk(item, buffer); }
                    catch (Exception ex) { errors[index] = ex; }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        if (memory is IConcurrentMemoryReads)
        {
            int workerCount = Math.Min(Math.Clamp(Environment.ProcessorCount, 4, 8), items.Count);
            // Dedicated bounded workers avoid thread-pool injection delays when loading already runs on a pool thread.
            var workers = Enumerable.Range(0, workerCount).Select(_ =>
                Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(workers);
        }
        else Worker();
        ct.ThrowIfCancellationRequested();
        if (errors.FirstOrDefault(e => e is not null) is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        return results.SelectMany(r => r ?? []).ToArray();

        IReadOnlyList<HeapVtableMatch> ScanChunk((int RegionIndex, ulong Start, int Length, bool IsRegionStart) item, byte[] buffer)
        {
            var found = new List<HeapVtableMatch>();
            ct.ThrowIfCancellationRequested();
            // A failed wide read is only a hint: retry bounded pages before declaring the index incomplete.
            if (memory.TryRead(item.Start, buffer.AsSpan(0, item.Length)))
                Match(item.Start, buffer.AsSpan(0, item.Length), found);
            else
                for (int page = 0; page < item.Length; page += 4096)
                {
                    ct.ThrowIfCancellationRequested();
                    int size = Math.Min(4096, item.Length - page);
                    if (size < 8) continue;
                    var bytes = buffer.AsSpan(page, size);
                    if (!memory.TryRead(item.Start + (ulong)page, bytes))
                    {
                        if (tolerateUnreadable) continue;
                        if (memory is IUnreadablePagesAreNotHeap)
                        {
                            // One VM entry is mapped one way: unreadable from its first page means device memory, so skip it.
                            if (item.IsRegionStart && page == 0)
                            {
                                Volatile.Write(ref skipRegion[item.RegionIndex], true);
                                return found;
                            }
                            continue;
                        }
                        throw new MemoryAccessException($"Heap index is incomplete: writable range 0x{item.Start + (ulong)page:X} ({size} bytes) could not be read");
                    }
                    Match(item.Start + (ulong)page, bytes, found);
                }
            return found;
        }
        void Match(ulong start, ReadOnlySpan<byte> bytes, List<HeapVtableMatch> found)
        {
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            int position = 0;
            if (Vector.IsHardwareAccelerated)
                for (; position + Vector<ulong>.Count <= words.Length; position += Vector<ulong>.Count)
                {
                    var block = new Vector<ulong>(words[position..]);
                    bool any = false;
                    foreach (var target in vectors)
                        if (Vector.EqualsAny(block, target)) { any = true; break; }
                    if (!any) continue;
                    for (int lane = 0; lane < Vector<ulong>.Count; lane++)
                        Add(position + lane, words[position + lane]);
                }
            for (; position < words.Length; position++) Add(position, words[position]);

            void Add(int at, ulong value)
            {
                if ((value - minVt) <= vtSpan && vtables.AsSpan().Contains(value))
                    found.Add(new(start + (ulong)at * 8, value));
            }
        }
    }

}
