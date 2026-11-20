using Fm27Editor.Core.Players;
using Fm27Editor.Core.Transfer;

namespace Fm27Editor.Core.Memory;

/// <summary>Stages a compound operation, including overlapping writes, then validates and commits it with rollback.
/// Reads see staged writes. This is not a lock on the game's own threads.</summary>
public sealed class MemoryWriteBatch(IProcessMemory memory, bool coalesceAdjacentWrites = true) : IProcessMemory
{
    readonly Dictionary<ulong, byte> original = [];
    readonly Dictionary<ulong, byte> pending = [];
    readonly List<ulong> order = [];
    readonly HashSet<ulong> writeStarts = [];

    public bool TryRead(ulong address, Span<byte> destination)
    {
        var bytes = new byte[destination.Length];
        if (!memory.TryRead(address, bytes)) return false;
        for (int i = 0; i < bytes.Length; i++)
        {
            ulong at = checked(address + (ulong)i);
            if (original.TryGetValue(at, out byte before) && before != bytes[i])
                throw new StaleObjectException("Game data changed while preparing the edit");
            original.TryAdd(at, bytes[i]);
            destination[i] = pending.GetValueOrDefault(at, bytes[i]);
        }
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        var before = new byte[data.Length];
        if (!TryRead(address, before)) return false;
        if (!coalesceAdjacentWrites) writeStarts.Add(address);
        for (int i = 0; i < data.Length; i++)
        {
            ulong at = checked(address + (ulong)i);
            if (!pending.ContainsKey(at)) order.Add(at);
            pending[at] = data[i];
        }
        return true;
    }

    public void Commit()
    {
        // Preserve first-write order (initialize buffers before publishing their pointers).
        var writes = new List<PlannedWrite>();
        for (int i = 0; i < order.Count;)
        {
            ulong start = order[i];
            int end = i + 1;
            while (end < order.Count && order[end] == order[end - 1] + 1 && !writeStarts.Contains(order[end])) end++;
            var addresses = order.GetRange(i, end - i);
            writes.Add(new(start, addresses.Select(a => original[a]).ToArray(), addresses.Select(a => pending[a]).ToArray(), "compound edit"));
            i = end;
        }
        var guards = new List<PlannedWrite>();
        var keys = original.Keys.Order().ToArray();
        for (int i = 0; i < keys.Length;)
        {
            int end = i + 1;
            while (end < keys.Length && keys[end] == keys[end - 1] + 1) end++;
            guards.Add(new(keys[i], keys[i..end].Select(a => original[a]).ToArray(), [], "compound edit snapshot"));
            i = end;
        }
        FutureTransferTransaction.Apply(memory, guards, writes);
    }

    public static T Run<T>(IProcessMemory memory, Func<IProcessMemory, T> prepare)
    {
        var batch = new MemoryWriteBatch(memory);
        T result = prepare(batch);
        batch.Commit();
        return result;
    }
    public static void Run(IProcessMemory memory, Action<IProcessMemory> prepare) =>
        Run(memory, staged => { prepare(staged); return true; });
}
