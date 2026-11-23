namespace Fm27Editor.Core.Memory;

/// <summary>
/// Read-ahead for one bounded, read-only operation. Nearby fields share a page read instead of each crossing the
/// process boundary. Dispose after materializing the result; never use this memory for live writes or reloads.
/// A scope and any nested scopes are confined to one thread; the shared cache is not synchronized.
/// </summary>
public sealed class BulkReadScope : IDisposable
{
    readonly ReadAheadMemory? owned;

    /// <summary>A read-only view. Nested scopes share the outer scope's cache and do not dispose it.</summary>
    public IProcessMemory Memory { get; }

    public BulkReadScope(IProcessMemory memory) : this(memory, 32768) { }

    internal BulkReadScope(IProcessMemory memory, int pageCount, int failedPageHintCount = 0)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfNegative(failedPageHintCount);
        Memory = memory is ReadAheadMemory ? memory : owned = new ReadAheadMemory(memory, pageCount, failedPageHintCount);
    }

    /// <summary>Merges contiguous 4 KiB pages covering <paramref name="addresses"/> into fewer larger reads.</summary>
    public void Prefetch(ReadOnlySpan<ulong> addresses, int spanBytes) =>
        (Memory as ReadAheadMemory)?.Prefetch(addresses, spanBytes);

    public void Dispose() => owned?.Dispose();
}

/// <summary>At most 128 MiB with the default scope; no cache survives the bulk operation.</summary>
internal sealed class ReadAheadMemory(IProcessMemory source, int pageCount, int failedPageHintCount) : IProcessMemory, IDisposable
{
    internal const int PageSize = 4096;
    readonly Dictionary<ulong, byte[]> pages = new();
    readonly Queue<ulong> order = new();
    readonly HashSet<ulong> failedPages = new();
    readonly Queue<ulong> failedOrder = new();
    byte[]? spare;
    bool disposed;

    internal int CachedBytes => pages.Count * PageSize;

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (buffer.IsEmpty) return true;
        // Vector arrays and code scans already read in bulk. Avoid both copying and evicting useful small reads.
        if (buffer.Length > PageSize) return source.TryRead(address, buffer);
        if (address > ulong.MaxValue - (ulong)(buffer.Length - 1)) return false;

        int copied = 0;
        while (copied < buffer.Length)
        {
            ulong at = address + (ulong)copied;
            ulong page = at & ~(ulong)(PageSize - 1);
            if (!pages.TryGetValue(page, out var bytes))
            {
                // Discovery follows many speculative pointers. A failed wide probe only disables read-ahead;
                // the precise requested bytes are always retried, including if memory has become readable.
                if (failedPages.Contains(page)) return source.TryRead(address, buffer);
                if (pages.Count == pageCount)
                {
                    ulong oldest = order.Dequeue();
                    spare = pages[oldest];
                    pages.Remove(oldest);
                }
                bytes = spare ?? new byte[PageSize];
                spare = null;
                if (!source.TryRead(page, bytes))
                {
                    spare = bytes;
                    if (failedPageHintCount > 0 && failedPages.Add(page))
                    {
                        if (failedOrder.Count == failedPageHintCount) failedPages.Remove(failedOrder.Dequeue());
                        failedOrder.Enqueue(page);
                    }
                    // A valid object/string can sit at an allocation boundary. A failed speculative page read
                    // must not turn a readable requested range into missing data, including across two pages.
                    return source.TryRead(address, buffer);
                }
                pages.Add(page, bytes);
                order.Enqueue(page);
            }
            int offset = (int)(at - page);
            int take = Math.Min(PageSize - offset, buffer.Length - copied);
            bytes.AsSpan(offset, take).CopyTo(buffer[copied..]);
            copied += take;
        }
        return true;
    }

    internal void Prefetch(ReadOnlySpan<ulong> addresses, int spanBytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (addresses.IsEmpty || spanBytes <= 0 || pageCount < 2) return;

        var unique = new HashSet<ulong>();
        ulong span = (ulong)spanBytes - 1;
        foreach (ulong addr in addresses)
        {
            if (addr == 0 || addr > ulong.MaxValue - span) continue;
            ulong firstPage = addr & ~(ulong)(PageSize - 1);
            ulong lastPage = (addr + span) & ~(ulong)(PageSize - 1);
            for (ulong p = firstPage; ; p += PageSize)
            {
                if (!pages.ContainsKey(p) && !failedPages.Contains(p))
                {
                    unique.Add(p);
                    if (unique.Count >= pageCount) break;
                }
                if (p == lastPage) break;
            }
            if (unique.Count >= pageCount) break;
        }
        if (unique.Count < 2) return;

        var sorted = new ulong[unique.Count];
        unique.CopyTo(sorted);
        Array.Sort(sorted);

        const int MaxRunPages = 64;
        const int MaxGapPages = 4;
        byte[]? scratch = null;

        void StoreCachedPage(ulong page, ReadOnlySpan<byte> data)
        {
            if (pages.ContainsKey(page)) return;
            if (pages.Count == pageCount)
            {
                ulong oldest = order.Dequeue();
                spare = pages[oldest];
                pages.Remove(oldest);
            }
            var block = spare ?? new byte[PageSize];
            spare = null;
            data.CopyTo(block);
            pages.Add(page, block);
            order.Enqueue(page);
        }

        int i = 0;
        while (i < sorted.Length)
        {
            int gStart = i;
            int gEnd = i;
            while (gEnd + 1 < sorted.Length)
            {
                ulong prev = sorted[gEnd];
                ulong next = sorted[gEnd + 1];
                if (next - prev > (ulong)(MaxGapPages + 1) * PageSize) break;
                if ((next - sorted[gStart]) / PageSize + 1 > MaxRunPages) break;
                gEnd++;
            }

            if (gEnd > gStart)
            {
                ulong groupStart = sorted[gStart];
                int spanPages = (int)((sorted[gEnd] - groupStart) / PageSize + 1);
                scratch ??= new byte[MaxRunPages * PageSize];
                if (source.TryRead(groupStart, scratch.AsSpan(0, spanPages * PageSize)))
                {
                    for (int k = gStart; k <= gEnd; k++)
                    {
                        ulong page = sorted[k];
                        int offsetPages = (int)((page - groupStart) / PageSize);
                        StoreCachedPage(page, scratch.AsSpan(offsetPages * PageSize, PageSize));
                    }
                }
                else
                {
                    int sub = gStart;
                    while (sub <= gEnd)
                    {
                        ulong runStart = sorted[sub];
                        int runCount = 1;
                        while (sub + runCount <= gEnd && sorted[sub + runCount] == runStart + (ulong)runCount * PageSize)
                            runCount++;
                        if (runCount >= 2 && source.TryRead(runStart, scratch.AsSpan(0, runCount * PageSize)))
                        {
                            for (int k = 0; k < runCount; k++)
                                StoreCachedPage(runStart + (ulong)k * PageSize, scratch.AsSpan(k * PageSize, PageSize));
                        }
                        sub += runCount;
                    }
                }
            }
            i = gEnd + 1;
        }
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> data) =>
        throw new NotSupportedException("Bulk-read memory cannot write; use the live guarded writer");

    public void Dispose()
    {
        disposed = true;
        pages.Clear();
        order.Clear();
        failedPages.Clear();
        failedOrder.Clear();
        spare = null;
    }
}
