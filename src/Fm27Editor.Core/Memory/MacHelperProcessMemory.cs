// MacHelperProcessMemory.cs — thread-safe, AOT-compatible client for the privileged Fm27EditorHelper daemon.
//
// Implements IProcessMemory, IMemoryRegions, IRemoteAllocator, IMemoryWriteObserver, ICodePatchHost and
// IDisposable over /var/run/fm27editor.sock using System.Net.Sockets.Socket(AddressFamily.Unix, ...).
// Thread-safe via lock around socket request/response and AOT-compatible (no reflection).
// Used by GameSession.Attach: when MacProcessMemory throws MemoryAccessException (non-root) and
// /var/run/fm27editor.sock exists, fall back to MacHelperProcessMemory.
//
// Wire protocol (all integers little-endian, matching fm27_helper.c):
//   request  = uint32 payload_length, then payload
//   payload  = uint8 opcode, then opcode arguments
//   response = uint32 payload_length, then payload
//   payload  = int32 status (0 ok, 1 error), then data
//   on error: int32 code, uint32 message_length, message bytes
//
// Opcodes:
//   1 Open(pid:i32, writable:u8)            -> handle:i32
//   2 Read(handle:i32, addr:u64, len:u32)   -> data bytes
//   3 Write(handle:i32, addr:u64, len:u32, data)
//   4 Allocate(handle:i32, bytes:u32)       -> addr:u64
//   5 AllocateNear(handle:i32, near:u64, bytes:u32) -> addr:u64
//   6 Protect(handle:i32, addr:u64, bytes:u32, vm_prot:u32, set_max:u8) -> old_page:u32
//   7 Free(handle:i32, addr:u64, bytes:u64)
//   8 Enumerate(handle:i32, start:u64, end:u64) -> count:u32, records
//   9 Close(handle:i32)
//  10 DyldInfo(handle:i32)                  -> info_addr:u64, info_size:u64
//  11 Pause(handle:i32)                     -> token:u32
//  12 Resume(handle:i32)
//  13 ReadMany(handle:i32, count:u32, [addr:u64, len:u32]) -> count:u32, [status:i32, len:u32, data]

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.Core.Memory;

public sealed class MacHelperProcessMemory : IUnreadablePagesAreNotHeap, IMacMemory, IMemoryRegions, IRemoteAllocator, IGameHeapAllocator, IMemoryWriteObserver, IMemoryAllocationObserver, IGameHeapVerifier, IWriteGate, ICodePatchHost, IScatterReadMemory, IVtableHeapScanner, IDisposable
{
    private const string SocketPath = "/var/run/fm27editor.sock";

    /// <summary>Whether the privileged helper is installed and listening; without it there is no fallback for a
    /// process this one may not open itself.</summary>
    public static bool Available => File.Exists(SocketPath);
    internal const int ReceiveTimeoutMs = 30000;

    private const int VmProtRead = 0x01, VmProtWrite = 0x02, VmProtExecute = 0x04, VmProtCopy = 0x10;
    private const int VmProtReadWrite = VmProtRead | VmProtWrite;
    private const int VmProtReadExecute = VmProtRead | VmProtExecute;
    private const int VmProtReadWriteCopy = VmProtRead | VmProtWrite | VmProtCopy;
    private const uint PageExecuteReadWrite = 0x40, PageExecuteRead = 0x20, PageReadWrite = 0x04, PageReadOnly = 0x02;

    private enum Op : byte
    {
        Open = 1,
        Read = 2,
        Write = 3,
        Allocate = 4,
        AllocateNear = 5,
        Protect = 6,
        Free = 7,
        Enumerate = 8,
        Close = 9,
        DyldInfo = 10,
        Pause = 11,
        Resume = 12,
        ReadMany = 13,
        RemoteCalloc = 14,
        HeapCheck = 15,
        ScanVtables = 16,
    }

    static readonly HashSet<int> MallocUserTags = [1, 2, 3, 4, 7, 8, 9, 11, 12, 13];

    private readonly object _gate = new();
    private readonly Dictionary<ulong, int> _allocations = [];
    private Socket? _socket;
    private int _handle;
    private bool _disposed;
    private bool? _nativeScatter;
    private bool? _nativeHeapCheck;
    private bool? _nativeScanVtables;

    // The write observer events are set by the edit history; the helper fires them through the client.
    public Action<ulong, byte[], byte[]>? OnWrite { get; set; }
    public Action<ulong, int>? OnAllocate { get; set; }
    public Action<ulong, int, AllocationOrigin>? OnAllocateWithOrigin { get; set; }

    /// <summary>Called before each write; throws to refuse it while the game loads a save or has none loaded. Kept
    /// outside <see cref="_gate"/> so the hook's own reads through this client can take the lock.</summary>
    public Action? BeforeWrite { get; set; }

    public int ProcessId { get; }
    public bool CanWrite { get; }

    public MacHelperProcessMemory(int processId, bool writable)
        : this(processId, writable, SocketPath, ReceiveTimeoutMs) { }

    internal MacHelperProcessMemory(int processId, bool writable, string socketPath, int timeoutMs)
    {
        ProcessId = processId;
        CanWrite = writable;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.ReceiveTimeout = timeoutMs;
            socket.SendTimeout = timeoutMs;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            _socket = socket;
            _handle = SendOpen(processId, writable);
        }
        catch
        {
            _handle = 0;
            _socket = null;
            try { socket.Close(); } catch { }
            socket.Dispose();
            throw;
        }
    }

    private int SendOpen(int pid, bool writable)
    {
        var payload = new byte[6];
        payload[0] = (byte)Op.Open;
        WriteInt32(payload, 1, pid);
        payload[5] = writable ? (byte)1 : (byte)0;
        var response = Exchange(payload);
        if (ReadInt32(response, 0) != 0)
            throw new MemoryAccessException($"Open(pid {pid}) failed: {ReadMessage(response)}");
        return ReadInt32(response, 4);
    }

    public unsafe bool TryRead(ulong address, Span<byte> buffer)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) return false;
            if (buffer.IsEmpty) return true;

            try
            {
                Span<byte> request = stackalloc byte[21];
                BinaryPrimitives.WriteUInt32LittleEndian(request, 17);
                request[4] = (byte)Op.Read;
                BinaryPrimitives.WriteInt32LittleEndian(request[5..], _handle);
                BinaryPrimitives.WriteUInt64LittleEndian(request[9..], address);
                BinaryPrimitives.WriteUInt32LittleEndian(request[17..], (uint)buffer.Length);
                SendExact(request);

                return ReceiveRead(buffer);
            }
            catch (Exception ex) when (ex is SocketException or MemoryAccessException or ObjectDisposedException)
            {
                // A partial frame must never be interpreted as the next reply on this connection.
                _socket?.Dispose();
                _socket = null;
                _handle = 0;
                throw;
            }
        }
    }

    bool ReceiveRead(Span<byte> buffer)
    {
        Span<byte> word = stackalloc byte[4];
        ReceiveExact(word);
        int length = BinaryPrimitives.ReadInt32LittleEndian(word);
        if (length < 4 || length > 512 * 1024 * 1024)
            throw new MemoryAccessException($"Helper returned an implausible response length {length}");
        if (length != (long)buffer.Length + 4)
        {
            Drain(length);
            return false;
        }
        // Keep small field reads to two receive calls without heap allocations. Large pages/chunks go
        // directly into the caller's destination, avoiding a second multi-megabyte buffer and copy.
        if (buffer.Length <= 256)
        {
            Span<byte> response = stackalloc byte[buffer.Length + 4];
            ReceiveExact(response);
            if (BinaryPrimitives.ReadInt32LittleEndian(response) != 0) return false;
            response[4..].CopyTo(buffer);
            return true;
        }
        ReceiveExact(word);
        if (BinaryPrimitives.ReadInt32LittleEndian(word) != 0)
        {
            Drain(length - 4);
            return false;
        }
        ReceiveExact(buffer);
        return true;
    }

    public void ReadMany(IReadOnlyList<MemoryReadRequest> requests, Span<bool> succeeded)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (succeeded.Length != requests.Count) throw new ArgumentException("One result is required per read", nameof(succeeded));
        // The discovery pipeline only uses fields, never large buffers: bounding replies prevents send/receive
        // deadlock with the existing helper, which sends each reply before reading the next request.
        if (requests.Any(r => r.Destination.Length is < 1 or > 32))
            throw new ArgumentException("Scatter reads require 1 to 32 bytes per destination", nameof(requests));
        succeeded.Clear();
        lock (_gate)
        {
            if (_socket == null || _disposed) return;
            try
            {
                if (_nativeScatter != false)
                {
                    for (int start = 0; start < requests.Count; start += MaxScatterReads)
                    {
                        int count = Math.Min(MaxScatterReads, requests.Count - start);
                        if (!NativeReadMany(requests, start, count, succeeded)) break;
                    }
                    if (_nativeScatter != false) return;
                }
                ReadManyLegacy(requests, succeeded);
            }
            catch (Exception ex) when (ex is SocketException or MemoryAccessException or ObjectDisposedException)
            {
                _socket?.Dispose();
                _socket = null;
                _handle = 0;
                throw;
            }
        }
    }

    public IReadOnlyList<HeapVtableMatch>? TryScanVtables(
        IReadOnlyList<MemoryRegion> regions,
        ReadOnlySpan<ulong> vtables,
        bool tolerateUnreadable,
        bool unreadablePagesAreNotHeap,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (vtables.Length == 0 || regions.Count == 0) return [];
        if (vtables.Length > 32 || regions.Count > 262144) return null;
        lock (_gate)
        {
            if (_nativeScanVtables == false || _socket == null || _disposed) return null;
            byte[] response;
            try
            {
                int payloadLen = 1 + 4 + 1 + 4 + vtables.Length * 8 + 4 + regions.Count * 16;
                var frame = new byte[4 + payloadLen];
                BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payloadLen);
                int at = 4;
                frame[at++] = (byte)Op.ScanVtables;
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(at), _handle); at += 4;
                frame[at++] = (byte)((tolerateUnreadable ? 1 : 0) | (unreadablePagesAreNotHeap ? 2 : 0));
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(at), vtables.Length); at += 4;
                for (int i = 0; i < vtables.Length; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(at), vtables[i]);
                    at += 8;
                }
                BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(at), regions.Count); at += 4;
                for (int i = 0; i < regions.Count; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(at), regions[i].Start); at += 8;
                    BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(at), regions[i].Size); at += 8;
                }
                SendExact(frame);

                Span<byte> word = stackalloc byte[4];
                ReceiveExact(word);
                int length = BinaryPrimitives.ReadInt32LittleEndian(word);
                if (length < 4 || length > 256 * 1024 * 1024)
                    throw new MemoryAccessException($"Helper returned an implausible ScanVtables response length {length}");
                response = new byte[length];
                ReceiveExact(response);
            }
            catch (Exception ex) when (ex is SocketException or MemoryAccessException or ObjectDisposedException)
            {
                _socket?.Dispose();
                _socket = null;
                _handle = 0;
                throw;
            }

            int status = ReadInt32(response, 0);
            if (status != 0)
            {
                if (_nativeScanVtables is null && response.Length >= 8 && ReadInt32(response, 4) == 14)
                {
                    _nativeScanVtables = false;
                    return null;
                }
                throw new MemoryAccessException(ReadMessage(response));
            }
            if (response.Length < 8)
                throw new MemoryAccessException("Malformed ScanVtables response");
            int count = ReadInt32(response, 4);
            if (count < 0 || response.Length != 8 + count * 16)
                throw new MemoryAccessException("Malformed ScanVtables result count");
            _nativeScanVtables = true;
            var matches = new HeapVtableMatch[count];
            int pos = 8;
            for (int i = 0; i < count; i++)
            {
                matches[i] = new HeapVtableMatch(ReadUInt64(response, pos), ReadUInt64(response, pos + 8));
                pos += 16;
            }
            return matches;
        }
    }

    const int MaxScatterReads = 1024, MaxScatterResponse = 8 + MaxScatterReads * 40;

    bool NativeReadMany(IReadOnlyList<MemoryReadRequest> requests, int start, int count, Span<bool> succeeded)
    {
        Span<byte> frame = stackalloc byte[13 + MaxScatterReads * 12];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)(9 + count * 12));
        frame[4] = (byte)Op.ReadMany;
        BinaryPrimitives.WriteInt32LittleEndian(frame[5..], _handle);
        BinaryPrimitives.WriteInt32LittleEndian(frame[9..], count);
        for (int i = 0; i < count; i++)
        {
            var request = requests[start + i];
            BinaryPrimitives.WriteUInt64LittleEndian(frame[(13 + i * 12)..], request.Address);
            BinaryPrimitives.WriteInt32LittleEndian(frame[(21 + i * 12)..], request.Destination.Length);
        }
        SendExact(frame[..(13 + count * 12)]);
        Span<byte> word = stackalloc byte[4];
        ReceiveExact(word);
        int length = BinaryPrimitives.ReadInt32LittleEndian(word);
        if (length < 4 || length > MaxScatterResponse) throw new MemoryAccessException("Invalid ReadMany response length");
        Span<byte> response = stackalloc byte[MaxScatterResponse];
        response = response[..length];
        ReceiveExact(response);
        int status = BinaryPrimitives.ReadInt32LittleEndian(response);
        if (status != 0)
        {
            if (status != 1 || length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(response[8..]) != (uint)(length - 12))
                throw new MemoryAccessException("Malformed ReadMany error response");
            // Older installed helpers explicitly return code 14 for an unknown opcode. Probe only once per
            // connection; no other error is hidden by falling back to the older transport.
            if (_nativeScatter is null && BinaryPrimitives.ReadInt32LittleEndian(response[4..]) == 14)
            {
                _nativeScatter = false;
                return false;
            }
            throw new MemoryAccessException($"ReadMany failed: {ReadMessage(response)}");
        }
        if (length < 8 || BinaryPrimitives.ReadInt32LittleEndian(response[4..]) != count)
            throw new MemoryAccessException("ReadMany returned the wrong result count");
        Span<int> offsets = stackalloc int[MaxScatterReads];
        int at = 8;
        // Validate the entire frame before publishing any destination bytes or success flags.
        for (int i = 0; i < count; i++)
        {
            if (at > length - 8) throw new MemoryAccessException("Truncated ReadMany result");
            int itemStatus = BinaryPrimitives.ReadInt32LittleEndian(response[at..]);
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(response[(at + 4)..]);
            at += 8;
            if (itemStatus == 0 && bytes == requests[start + i].Destination.Length && bytes <= length - at)
                offsets[i] = at;
            else if (itemStatus == 1 && bytes == 0) offsets[i] = -1;
            else throw new MemoryAccessException("Invalid ReadMany result length or status");
            at += bytes;
        }
        if (at != length) throw new MemoryAccessException("Unexpected trailing ReadMany data");
        _nativeScatter = true;
        for (int i = 0; i < count; i++)
        {
            succeeded[start + i] = offsets[i] >= 0;
            if (offsets[i] >= 0)
                response.Slice(offsets[i], requests[start + i].Destination.Length).CopyTo(requests[start + i].Destination.Span);
        }
        return true;
    }

    void ReadManyLegacy(IReadOnlyList<MemoryReadRequest> requests, Span<bool> succeeded)
    {
        int batchSize = 1;
        try { batchSize = Math.Clamp(_socket!.SendBufferSize / (4 * 21), 1, 64); }
        catch (SocketException) { } // If the platform cannot report its budget, use one outstanding request.
        Span<byte> wire = stackalloc byte[64 * 21];
        Span<byte> received = stackalloc byte[8192];
        var replies = new BufferedReplies(_socket!, received);
        for (int start = 0; start < requests.Count; start += batchSize)
        {
            int count = Math.Min(batchSize, requests.Count - start);
            for (int i = 0; i < count; i++)
            {
                var item = requests[start + i];
                var frame = wire.Slice(i * 21, 21);
                BinaryPrimitives.WriteUInt32LittleEndian(frame, 17);
                frame[4] = (byte)Op.Read;
                BinaryPrimitives.WriteInt32LittleEndian(frame[5..], _handle);
                BinaryPrimitives.WriteUInt64LittleEndian(frame[9..], item.Address);
                BinaryPrimitives.WriteUInt32LittleEndian(frame[17..], (uint)item.Destination.Length);
            }
            SendExact(wire[..(count * 21)]);
            for (int i = 0; i < count; i++)
                succeeded[start + i] = replies.Read(requests[start + i].Destination.Span);
            if (!replies.IsEmpty) throw new MemoryAccessException("The helper sent unexpected extra response bytes");
        }
    }

    /// <summary>Opportunistic buffering for the bounded small-field pipeline, confined to its socket lock.</summary>
    ref struct BufferedReplies
    {
        readonly Socket socket;
        readonly Span<byte> scratch;
        int begin, end;
        internal BufferedReplies(Socket socket, Span<byte> scratch)
        {
            this.socket = socket;
            this.scratch = scratch;
            begin = end = 0;
        }
        public readonly bool IsEmpty => begin == end;

        public bool Read(Span<byte> destination)
        {
            Span<byte> word = stackalloc byte[4];
            Copy(word);
            int length = BinaryPrimitives.ReadInt32LittleEndian(word);
            if (length < 4 || length > 512 * 1024 * 1024)
                throw new MemoryAccessException($"Helper returned an implausible response length {length}");
            if (length != (long)destination.Length + 4)
            {
                Drain(length);
                return false;
            }
            Copy(word);
            if (BinaryPrimitives.ReadInt32LittleEndian(word) != 0)
            {
                Drain(length - 4);
                return false;
            }
            Copy(destination);
            return true;
        }

        void Copy(scoped Span<byte> destination)
        {
            while (!destination.IsEmpty)
            {
                Fill();
                int count = Math.Min(end - begin, destination.Length);
                scratch.Slice(begin, count).CopyTo(destination);
                begin += count;
                destination = destination[count..];
            }
        }

        void Drain(int remaining)
        {
            while (remaining > 0)
            {
                Fill();
                int count = Math.Min(end - begin, remaining);
                begin += count;
                remaining -= count;
            }
        }

        void Fill()
        {
            if (begin < end) return;
            begin = 0;
            // Receive whatever is currently available. Never wait for a guessed aggregate frame size: the
            // first four bytes alone may be an invalid length that must fail immediately.
            end = socket.Receive(scratch, SocketFlags.None);
            if (end == 0) throw new MemoryAccessException("The helper disconnected");
        }
    }

    public unsafe bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        BeforeWrite?.Invoke();
        lock (_gate)
        {
            if (_socket == null || _disposed) return false;
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
            if (data.IsEmpty) return true;

            // Read the old bytes first so the edit history can record them, like MacProcessMemory does.
            byte[]? before = null;
            var listener = OnWrite;
            if (listener is not null)
            {
                before = new byte[data.Length];
                if (!TryRead(address, before)) before = null;
            }

            var payload = new byte[17 + data.Length];
            payload[0] = (byte)Op.Write;
            WriteInt32(payload, 1, _handle);
            WriteUInt64(payload, 5, address);
            WriteUInt32(payload, 13, (uint)data.Length);
            data.CopyTo(payload.AsSpan(17));

            var response = Exchange(payload);
            bool ok = ReadInt32(response, 0) == 0;
            if (ok && listener is not null && before is not null) listener(address, before, data.ToArray());
            return ok;
        }
    }

    /// <summary>calloc(1, bytes) inside the game through the helper's RemoteCalloc op.</summary>
    public ulong AllocateFromGameHeap(int bytes)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) throw new MemoryAccessException("The process is no longer attached");
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));

            var payload = new byte[9];
            payload[0] = (byte)Op.RemoteCalloc;
            WriteInt32(payload, 1, _handle);
            WriteUInt32(payload, 5, (uint)bytes);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"Game heap allocation of {bytes} bytes failed: {ReadMessage(response)}");
            ulong address = ReadUInt64(response, 4);
            OnAllocateWithOrigin?.Invoke(address, bytes, AllocationOrigin.GameHeap);
            OnAllocate?.Invoke(address, bytes);
            return address;
        }
    }

    public ulong Allocate(int bytes)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) throw new MemoryAccessException("The process is no longer attached");
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));

            var payload = new byte[9];
            payload[0] = (byte)Op.Allocate;
            WriteInt32(payload, 1, _handle);
            WriteUInt32(payload, 5, (uint)bytes);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"Allocate({bytes}) failed: {ReadMessage(response)}");
            ulong address = ReadUInt64(response, 4);
            OnAllocateWithOrigin?.Invoke(address, bytes, AllocationOrigin.RawVm);
            OnAllocate?.Invoke(address, bytes);
            return address;
        }
    }

    public HeapBlockCheck VerifyGameHeapBlock(ulong address, int requestedBytes)
    {
        if (address == 0)
            return new(false, "mach-tag+malloc_size", null, null, "null pointer");

        bool tryNative;
        lock (_gate) tryNative = _nativeHeapCheck != false;
        if (tryNative)
        {
            lock (_gate)
            {
                if (_socket is not null && !_disposed)
                {
                    var payload = new byte[13];
                    payload[0] = (byte)Op.HeapCheck;
                    WriteInt32(payload, 1, _handle);
                    WriteUInt64(payload, 5, address);
                    var response = Exchange(payload);
                    if (ReadInt32(response, 0) == 0 && response.Length >= 32)
                    {
                        _nativeHeapCheck = true;
                        int userTag = (int)ReadUInt32(response, 4);
                        ulong mallocSize = ReadUInt64(response, 8);
                        ulong regStart = ReadUInt64(response, 16);
                        ulong regSize = ReadUInt64(response, 24);
                        if (!MallocUserTags.Contains(userTag))
                            return new(false, "mach-tag+malloc_size", userTag, mallocSize > 0 ? mallocSize : null,
                                $"VM region 0x{regStart:X}+0x{regSize:X} has user_tag={userTag} (not VM_MEMORY_MALLOC*)");
                        if (mallocSize == 0)
                            return new(false, "mach-tag+malloc_size", userTag, 0,
                                $"malloc_size(0x{address:X}) returned 0 (user_tag={userTag})");
                        if (mallocSize < (ulong)requestedBytes)
                            return new(false, "mach-tag+malloc_size", userTag, mallocSize,
                                $"malloc_size={mallocSize} < requested={requestedBytes} (user_tag={userTag})");
                        return new(true, "mach-tag+malloc_size", userTag, mallocSize,
                            $"user_tag={userTag}, malloc_size={mallocSize} >= {requestedBytes}");
                    }
                    int code = response.Length >= 8 ? ReadInt32(response, 4) : 0;
                    if (code == 14) // unknown opcode on older installed daemon
                    {
                        _nativeHeapCheck = false;
                    }
                    else
                    {
                        _nativeHeapCheck = true;
                        return new(false, "mach-tag+malloc_size", null, null, ReadMessage(response));
                    }
                }
            }
        }

        // Fallback when running against a helper daemon built prior to OP_HEAP_CHECK:
        // verify 16-byte malloc alignment and writable non-module region not matching a raw 16 KB mach_vm_allocate page.
        if (address < 0x1_0000_0000UL || (address & 0xF) != 0)
            return new(false, "helper-origin+region", null, null, $"address 0x{address:X} is not a 16-byte aligned heap pointer");
        foreach (var r in EnumerateWritableRegions())
        {
            if (r.IsModuleData || address < r.Start || address >= r.Start + r.Size) continue;
            if (address == r.Start && r.Size == 0x4000)
                return new(false, "helper-origin+region", null, null,
                    $"0x{address:X} sits at offset 0 of a standalone 16KB VM page (raw mach_vm_allocate signature)");
            if (address + (ulong)requestedBytes > r.Start + r.Size)
                return new(false, "helper-origin+region", null, null,
                    $"0x{address:X}+{requestedBytes} crosses end of region 0x{r.Start + r.Size:X}");
            return new(true, "helper-origin+region", null, null,
                $"aligned block inside 0x{r.Start:X}+0x{r.Size:X} (daemon lacks OP_HEAP_CHECK; origin-gated)");
        }
        return new(false, "helper-origin+region", null, null, $"0x{address:X} not found in any writable heap region");
    }

    ulong ICodePatchHost.AllocateNear(ulong near, int bytes)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) throw new MemoryAccessException("The process is no longer attached");
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));

            var payload = new byte[21];
            payload[0] = (byte)Op.AllocateNear;
            WriteInt32(payload, 1, _handle);
            WriteUInt64(payload, 5, near);
            WriteUInt32(payload, 13, (uint)bytes);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"AllocateNear(0x{near:X}, {bytes}) failed: {ReadMessage(response)}");
            ulong address = ReadUInt64(response, 4);
            _allocations[address] = bytes;
            OnAllocate?.Invoke(address, bytes);
            return address;
        }
    }

    public void CommitExecutable(ulong address, int bytes) =>
        SendProtect(address, bytes, VmProtReadExecute);

    public uint Protect(ulong address, int bytes, uint protect) =>
        SendProtect(address, bytes, ToVmProtection(protect));

    private uint SendProtect(ulong address, int bytes, int vmProtection)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) throw new MemoryAccessException("The process is no longer attached");
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");

            var payload = new byte[22];
            payload[0] = (byte)Op.Protect;
            WriteInt32(payload, 1, _handle);
            WriteUInt64(payload, 5, address);
            WriteUInt32(payload, 13, (uint)bytes);
            WriteUInt32(payload, 17, unchecked((uint)vmProtection));
            payload[21] = 0; // set_max = false

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"Protect(0x{address:X}, {bytes}) failed: {ReadMessage(response)}");
            return ReadUInt32(response, 4);
        }
    }

    private static int ToVmProtection(uint protect) => protect switch
    {
        PageExecuteReadWrite => VmProtReadWriteCopy,
        PageExecuteRead => VmProtReadExecute,
        PageReadWrite => VmProtReadWrite,
        PageReadOnly => VmProtRead,
        _ => throw new NotSupportedException($"Unsupported page protection 0x{protect:X}"),
    };

    public void Free(ulong address)
    {
        lock (_gate)
        {
            if (_socket == null || _disposed) return;
            if (!CanWrite) throw new InvalidOperationException("Process was opened read-only");

            _allocations.Remove(address, out int trackedBytes);
            var payload = new byte[21];
            payload[0] = (byte)Op.Free;
            WriteInt32(payload, 1, _handle);
            WriteUInt64(payload, 5, address);
            WriteUInt64(payload, 13, trackedBytes > 0 ? (ulong)trackedBytes : 0);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"Free(0x{address:X}) failed: {ReadMessage(response)}");
        }
    }

    public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
    {
        lock (_gate)
        {
            var regions = new List<MemoryRegion>();
            if (_socket == null || _disposed) return regions;

            var payload = new byte[21];
            payload[0] = (byte)Op.Enumerate;
            WriteInt32(payload, 1, _handle);
            WriteUInt64(payload, 5, imageStart);
            WriteUInt64(payload, 13, imageEnd);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0) return regions;
            uint count = ReadUInt32(response, 4);
            var body = response.AsSpan(8);
            for (uint i = 0; i < count; i++)
            {
                int offset = (int)(i * 17);
                ulong start = ReadUInt64(body, offset);
                ulong size = ReadUInt64(body, offset + 8);
                bool isModuleData = body[offset + 16] != 0;
                regions.Add(new MemoryRegion(start, size, isModuleData));
            }
            return regions;
        }
    }

    public void GetTaskDyldInfo(out ulong allImageInfoAddress, out ulong allImageInfoSize)
    {
        lock (_gate)
        {
            var payload = new byte[5];
            payload[0] = (byte)Op.DyldInfo;
            WriteInt32(payload, 1, _handle);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"DyldInfo failed: {ReadMessage(response)}");
            allImageInfoAddress = ReadUInt64(response, 4);
            allImageInfoSize = ReadUInt64(response, 12);
        }
    }

    /// <summary>The task's dyld_all_image_infos address and size; false when task_info cannot provide them.</summary>
    public bool TryGetTaskDyldInfo(out ulong allImageInfoAddress, out ulong allImageInfoSize)
    {
        allImageInfoAddress = allImageInfoSize = 0;
        try
        {
            GetTaskDyldInfo(out allImageInfoAddress, out allImageInfoSize);
            return allImageInfoAddress != 0;
        }
        catch (MemoryAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads a NUL-terminated UTF-8 string, for dyld paths and other remote strings.</summary>
    public string? TryReadCString(ulong address, int maxLength = 1024)
    {
        if (address == 0 || maxLength <= 0) return null;
        var buffer = new byte[maxLength];
        if (!TryRead(address, buffer)) return null;
        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = maxLength;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    public IDisposable Pause()
    {        lock (_gate)
        {
            var payload = new byte[5];
            payload[0] = (byte)Op.Pause;
            WriteInt32(payload, 1, _handle);

            var response = Exchange(payload);
            if (ReadInt32(response, 0) != 0)
                throw new MemoryAccessException($"Pause failed: {ReadMessage(response)}");
            return new ResumeToken(this);
        }
    }

    public void FlushCode(ulong address, int bytes)
    {
        // No cross-process instruction-cache invalidation exists on macOS.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            if (_socket is not null && _handle > 0)
            {
                try
                {
                    var payload = new byte[5];
                    payload[0] = (byte)Op.Close;
                    WriteInt32(payload, 1, _handle);
                    Exchange(payload);
                }
                catch { }
            }
            _handle = 0;
            _socket?.Close();
            _socket?.Dispose();
            _socket = null;
        }
    }

    private byte[] Exchange(byte[] payload)
    {
        // request = uint32 payload_length, then payload
        var request = new byte[4 + payload.Length];
        WriteUInt32(request, 0, (uint)payload.Length);
        payload.CopyTo(request.AsSpan(4));
        SendExact(request);

        var lengthBuffer = new byte[4];
        ReceiveExact(lengthBuffer);
        int length = BitConverter.ToInt32(lengthBuffer);
        if (length < 4 || length > 512 * 1024 * 1024)
            throw new MemoryAccessException($"Helper returned an implausible response length {length}");
        var response = new byte[length];
        ReceiveExact(response);
        return response;
    }

    private void SendExact(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int sent = _socket!.Send(buffer, SocketFlags.None);
            if (sent == 0) throw new MemoryAccessException("The helper disconnected");
            buffer = buffer[sent..];
        }
    }

    private void Drain(int length)
    {
        Span<byte> discarded = stackalloc byte[4096];
        while (length > 0)
        {
            int take = Math.Min(length, discarded.Length);
            ReceiveExact(discarded[..take]);
            length -= take;
        }
    }

    private void ReceiveExact(Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int got = _socket!.Receive(buffer[offset..], SocketFlags.None);
            if (got == 0) throw new MemoryAccessException("The helper disconnected");
            offset += got;
        }
    }

    private sealed class ResumeToken(MacHelperProcessMemory owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            lock (owner._gate)
            {
                var payload = new byte[5];
                payload[0] = (byte)Op.Resume;
                WriteInt32(payload, 1, owner._handle);
                owner.Exchange(payload);
            }
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) => WriteUInt32(buffer, offset, (uint)value);
    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
    {
        WriteUInt32(buffer, offset, (uint)value);
        WriteUInt32(buffer, offset + 4, (uint)(value >> 32));
    }
    private static int ReadInt32(ReadOnlySpan<byte> buffer, int offset) => (int)ReadUInt32(buffer, offset);
    private static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset) =>
        (uint)buffer[offset] | ((uint)buffer[offset + 1] << 8) | ((uint)buffer[offset + 2] << 16) | ((uint)buffer[offset + 3] << 24);
    private static ulong ReadUInt64(ReadOnlySpan<byte> buffer, int offset) =>
        (ulong)ReadUInt32(buffer, offset) | ((ulong)ReadUInt32(buffer, offset + 4) << 32);
    private static string ReadMessage(ReadOnlySpan<byte> response)
    {
        // error payload = int32 code, uint32 message_length, message bytes
        if (response.Length < 12) return "unknown helper error";
        int code = ReadInt32(response, 4);
        uint length = ReadUInt32(response, 8);
        if (length == 0 || response.Length < 12 + (int)length) return $"helper error {code}";
        return Encoding.UTF8.GetString(response.Slice(12, (int)length));
    }
}
