using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Offsets;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Memory;

/// <summary>Preserves read access and history for a restricted profile, without exposing allocation or code patching.</summary>
internal sealed class VerifiedWriteMemory(IProcessMemory source, OffsetProfile profile, ulong moduleBase)
    : IMacMemory, IMemoryRegions, IScatterReadMemory, IVtableHeapScanner, IMemoryWriteObserver
{
    sealed record Target(PlayerRecord Player, bool IsLoan);

    readonly object writeGate = new();
    Dictionary<(ulong Address, int Width), Target?>? targets;
    (ulong Begin, ulong End) epoch;
    IMemoryWriteObserver? Observer => source as IMemoryWriteObserver;

    public bool TryRead(ulong address, Span<byte> buffer) => source.TryRead(address, buffer);

    public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        lock (writeGate)
        {
            var current = PlayerList();
            if (current.Begin == 0 || current.End < current.Begin)
                throw new StaleObjectException("The player list is unavailable. Load Game before editing.");
            if (targets is null)
            {
                var found = IndexTargets();
                if (PlayerList() != current) throw new StaleObjectException("The save changed while locating editable fields.");
                epoch = current;
                targets = found;
            }
            if (current != epoch) throw new StaleObjectException("The player list changed. Load Game before editing.");
            if (!targets.TryGetValue((address, data.Length), out var target) || target is null)
                throw new NotSupportedException("This field has no verified write layout on this build.");
            if (!IsCurrent(target, address)) throw new StaleObjectException("The player or loan changed since it was read.");
            return source.TryWrite(address, data);
        }
    }

    (ulong Begin, ulong End) PlayerList()
    {
        ulong root = moduleBase + profile.PlayerListRootRva;
        return (source.ReadUInt64(root + (ulong)profile.PlayerListBeginOffset), source.ReadUInt64(root + (ulong)profile.PlayerListEndOffset));
    }

    Dictionary<(ulong Address, int Width), Target?> IndexTargets()
    {
        var found = new Dictionary<(ulong Address, int Width), Target?>();
        using var reads = new BulkReadScope(source);
        var read = reads.Memory;
        var loans = new CurrentLoanReader(read, profile, moduleBase);
        bool passing = profile.VerifiedWrites.Contains(VerifiedWrite.Passing) && profile.Attributes.ContainsKey(PlayerAttribute.Passing);
        var caps = profile.PlayerFields?.GetValueOrDefault(PlayerField.Caps);
        bool capsAllowed = profile.VerifiedWrites.Contains(VerifiedWrite.Caps) &&
            caps is { Path.Count: 1, Type: ClubFieldType.Int16, Scale: 1, Mask: 0 };
        bool fee = profile.VerifiedWrites.Contains(VerifiedWrite.LoanPlayingMonthlyFee) && profile.Relations is not null;
        foreach (var player in new PlayerRepository(read, profile, moduleBase).LoadAll())
        {
            if (player.Id is not > 0) continue;
            var owner = PersonOwner.Of(player, profile);
            if (passing) Add(owner.At(profile.Attributes[PlayerAttribute.Passing]), 1, new(player, false));
            if (capsAllowed) Add(owner.At(caps!.Path[0]), 2, new(player, false));
            if (fee && loans.Read(player) is { Status: CurrentLoanStatus.Present, Contract: { } contract } &&
                OwnsLoan(read, owner, contract.Address))
                Add(contract.Address + LoanEditor.PlayingMonthlyFee, 4, new(player, true));
        }
        return found;

        void Add(ulong address, int width, Target target)
        {
            if (!found.TryAdd((address, width), target)) found[(address, width)] = null;
        }
    }

    bool IsCurrent(Target target, ulong address)
    {
        var player = target.Player;
        var owner = PersonOwner.Of(player, profile);
        Span<byte> id = stackalloc byte[4];
        if (profile.Fields.Id is not { } offset || !owner.IsValid(source, moduleBase) ||
            !source.TryRead(owner.At(offset), id) || BitConverter.ToInt32(id) != player.Id) return false;
        return !target.IsLoan || new CurrentLoanReader(source, profile, moduleBase).Read(player) is
            { Status: CurrentLoanStatus.Present, Contract: { } loan } &&
            loan.Address + LoanEditor.PlayingMonthlyFee == address && OwnsLoan(source, owner, loan.Address);
    }

    bool OwnsLoan(IProcessMemory memory, PersonOwner owner, ulong contract)
    {
        Span<byte> person = stackalloc byte[8];
        return memory.TryRead(contract + (ulong)LoanCreator.ContractPerson, person) &&
            BitConverter.ToUInt64(person) == owner.At(profile.Relations!.PlayerPerson);
    }

    public void ReadMany(IReadOnlyList<MemoryReadRequest> requests, Span<bool> succeeded)
    {
        if (source is IScatterReadMemory scatter) scatter.ReadMany(requests, succeeded);
        else for (int i = 0; i < requests.Count; i++) succeeded[i] = TryRead(requests[i].Address, requests[i].Destination.Span);
    }

    public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0) =>
        source is IMemoryRegions regions ? regions.EnumerateWritableRegions(imageStart, imageEnd) : [];

    public IReadOnlyList<HeapVtableMatch>? TryScanVtables(
        IReadOnlyList<MemoryRegion> regions,
        ReadOnlySpan<ulong> vtables,
        bool tolerateUnreadable,
        bool unreadablePagesAreNotHeap,
        CancellationToken ct) =>
        source is IVtableHeapScanner scanner
            ? scanner.TryScanVtables(regions, vtables, tolerateUnreadable, unreadablePagesAreNotHeap || source is IUnreadablePagesAreNotHeap, ct)
            : null;

    public bool TryGetTaskDyldInfo(out ulong address, out ulong size)
    {
        address = size = 0;
        return source is IMacMemory mac && mac.TryGetTaskDyldInfo(out address, out size);
    }

    public string? TryReadCString(ulong address, int maxLength = 1024) =>
        (source as IMacMemory)?.TryReadCString(address, maxLength);

    public Action<ulong, byte[], byte[]>? OnWrite
    {
        get => Observer?.OnWrite;
        set { if (Observer is { } observer) observer.OnWrite = value; }
    }

    public Action<ulong, int>? OnAllocate
    {
        get => null;
        set { }
    }

    public void Dispose() => (source as IDisposable)?.Dispose();
}
