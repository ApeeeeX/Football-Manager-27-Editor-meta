using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace Fm27Editor.Core.Memory;

public readonly record struct HeapVtableMatch(ulong Address, ulong Vtable);

/// <summary>One bounded, read-only heap pass for multiple object types. Results preserve region and word order.</summary>
public static class HeapVtableScanner
{
    public static IReadOnlyList<HeapVtableMatch> Scan(IProcessMemory memory, params ulong[] vtables) =>
        ScanCore(memory, vtables, tolerateUnreadable: false, CancellationToken.None);

    /// <summary>Discovers candidates in a live heap where regions can disappear while being read.</summary>
    internal static IReadOnlyList<HeapVtableMatch> ScanReadable(IProcessMemory memory, ulong vtable, CancellationToken ct) =>
        ScanCore(memory, [vtable], tolerateUnreadable: true, ct);

    static IReadOnlyList<HeapVtableMatch> ScanCore(IProcessMemory memory, ulong[] vtables, bool tolerateUnreadable, CancellationToken ct)
    {
        if (memory is not IMemoryRegions regions)
            throw new NotSupportedException("Heap discovery requires memory region enumeration");
        var readable = regions.EnumerateWritableRegions().Where(r => !r.IsModuleData && !r.IsGuard).ToArray();
        if (memory is IVtableHeapScanner nativeScanner &&
            nativeScanner.TryScanVtables(readable, vtables, tolerateUnreadable, memory is IUnreadablePagesAreNotHeap, ct) is { } fastMatches)
            return fastMatches;
        var vectors = vtables.Select(v => new Vector<ulong>(v)).ToArray();
        var results = new IReadOnlyList<HeapVtableMatch>[readable.Length];
        var errors = new Exception?[readable.Length];
        // Windows ReadProcessMemory supports independent concurrent reads. Other providers remain serial.
        // Four 4 MiB buffers bound active raw storage; region results are joined in original enumeration order.
        int next = -1;
        void Worker()
        {
            var buffer = new byte[1 << 22];
            int index;
            while (!ct.IsCancellationRequested && (index = Interlocked.Increment(ref next)) < readable.Length)
            {
                try { results[index] = ScanRegion(readable[index], buffer); }
                catch (Exception ex) { errors[index] = ex; }
            }
        }
        if (memory is IConcurrentMemoryReads)
        {
            // Dedicated bounded workers avoid thread-pool injection delays when loading already runs on a pool thread.
            var workers = Enumerable.Range(0, Math.Min(4, readable.Length)).Select(_ =>
                Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Task.WaitAll(workers);
        }
        else Worker();
        ct.ThrowIfCancellationRequested();
        if (errors.FirstOrDefault(e => e is not null) is { } error) ExceptionDispatchInfo.Capture(error).Throw();
        return results.SelectMany(r => r).ToArray();

        IReadOnlyList<HeapVtableMatch> ScanRegion(MemoryRegion region, byte[] buffer)
        {
            var found = new List<HeapVtableMatch>();
            for (ulong offset = 0; offset + 8 <= region.Size; offset += (ulong)buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                int length = (int)Math.Min((ulong)buffer.Length, region.Size - offset);
                // A failed wide read is only a hint: retry bounded pages before declaring the index incomplete.
                if (memory.TryRead(region.Start + offset, buffer.AsSpan(0, length)))
                    Match(region.Start + offset, buffer.AsSpan(0, length), found);
                else
                    for (int page = 0; page < length; page += 4096)
                    {
                        ct.ThrowIfCancellationRequested();
                        int size = Math.Min(4096, length - page);
                        if (size < 8) continue;
                        var bytes = buffer.AsSpan(page, size);
                        if (!memory.TryRead(region.Start + offset + (ulong)page, bytes))
                        {
                            if (tolerateUnreadable) continue;
                            if (memory is IUnreadablePagesAreNotHeap)
                            {
                                // One VM entry is mapped one way: unreadable from its first page means device memory, so skip it.
                                if (offset == 0 && page == 0) return found;
                                continue;
                            }
                            throw new MemoryAccessException($"Heap index is incomplete: writable range 0x{region.Start + offset + (ulong)page:X} ({size} bytes) could not be read");
                        }
                        Match(region.Start + offset + (ulong)page, bytes, found);
                    }
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
                if (vtables.AsSpan().Contains(value)) found.Add(new(start + (ulong)at * 8, value));
            }
        }
    }

}
