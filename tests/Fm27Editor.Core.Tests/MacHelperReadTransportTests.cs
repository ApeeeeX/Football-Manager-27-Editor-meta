using System.Buffers.Binary;
using System.Net.Sockets;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Tests;

public class MacHelperReadTransportTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(8192)]
    public async Task Reads_fragmented_frames_without_losing_bytes(int length)
    {
        if (!OperatingSystem.IsMacOS()) return;
        byte[] expected = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
        await WithServer(async socket =>
        {
            var request = await Request(socket);
            Assert.Equal(2, request[0]);
            Assert.Equal(0x12345678UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(5)));
            Assert.Equal(length, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(13)));
            await Reply(socket, 0, expected, fragmented: true);
        }, helper => Assert.Equal(expected, helper.Read(0x12345678, length)));
    }

    [Fact]
    public async Task Error_and_mismatched_success_are_drained_before_the_next_request()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            await Reply(socket, 1, new byte[9000], fragmented: true);
            await Request(socket);
            await Reply(socket, 0, new byte[5000]);
            await Request(socket);
            await Reply(socket, 0, new byte[] { 2, 4, 6, 8 });
        }, helper =>
        {
            Assert.False(helper.TryRead(0x10000, new byte[4]));
            Assert.False(helper.TryRead(0x10000, new byte[4]));
            Assert.Equal(new byte[] { 2, 4, 6, 8 }, helper.Read(0x10000, 4));
        });
    }

    [Theory]
    [InlineData(8)]
    [InlineData(8192)]
    public async Task Same_size_error_preserves_destination_and_connection_alignment(int length)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            await Reply(socket, 1, new byte[length]);
            await Request(socket);
            await Reply(socket, 0, new byte[] { 31 });
        }, helper =>
        {
            var destination = Enumerable.Repeat((byte)0xA5, length).ToArray();
            Assert.False(helper.TryRead(0x10000, destination));
            Assert.All(destination, b => Assert.Equal(0xA5, b));
            Assert.Equal(new byte[] { 31 }, helper.Read(0x10000, 1));
        });
    }

    [Theory]
    [InlineData(8)]
    [InlineData(8192)]
    public async Task Truncated_frames_close_the_connection(int length)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            var truncated = new byte[9];
            BinaryPrimitives.WriteInt32LittleEndian(truncated, length + 4);
            truncated[8] = 51;
            await Send(socket, truncated);
            socket.Shutdown(SocketShutdown.Both);
        }, helper =>
        {
            Assert.Throws<MemoryAccessException>(() => helper.Read(0x10000, length));
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Implausible_frame_size_closes_without_allocating_or_draining_it()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            await Send(socket, BitConverter.GetBytes(int.MaxValue));
        }, helper =>
        {
            Assert.Throws<MemoryAccessException>(() => helper.Read(0x10000, 1));
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Write_observer_still_receives_the_live_before_and_after_bytes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            Assert.Equal(2, (await Request(socket))[0]);
            await Reply(socket, 0, new byte[] { 1, 2, 3 });
            var write = await Request(socket);
            Assert.Equal(3, write[0]);
            Assert.Equal(new byte[] { 4, 5, 6 }, write[17..]);
            await Reply(socket, 0, []);
        }, helper =>
        {
            byte[]? old = null, changed = null;
            helper.OnWrite = (_, before, after) => { old = before; changed = after; };
            Assert.True(helper.TryWrite(0x10000, new byte[] { 4, 5, 6 }));
            Assert.Equal(new byte[] { 1, 2, 3 }, old);
            Assert.Equal(new byte[] { 4, 5, 6 }, changed);
        });
    }

    [Fact]
    public async Task Scatter_read_keeps_order_and_drains_middle_errors_across_multiple_batches()
    {
        if (!OperatingSystem.IsMacOS()) return;
        const int count = 145;
        await WithServer(async socket =>
        {
            // Match the real helper: send the response immediately after each request, before reading the next.
            for (int i = 0; i < count; i++)
            {
                var request = await Request(socket);
                Assert.Equal(2, request[0]);
                Assert.Equal((ulong)(0x10000 + i % 7), BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(5)));
                if (i == 70) await Reply(socket, 1, new byte[37], fragmented: true);
                else await Reply(socket, 0, BitConverter.GetBytes(i), fragmented: i is 0 or 144);
            }
            await Request(socket);
            await Reply(socket, 0, new byte[] { 99 });
        }, helper =>
        {
            var values = new byte[count][];
            var requests = new MemoryReadRequest[count];
            for (int i = 0; i < count; i++) requests[i] = new((ulong)(0x10000 + i % 7), values[i] = new byte[4]);
            var result = new bool[count];
            helper.ReadMany(requests, result);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(i != 70, result[i]);
                if (result[i]) Assert.Equal(i, BitConverter.ToInt32(values[i]));
            }
            Assert.Equal(new byte[] { 99 }, helper.Read(0x10000, 1));
        });
    }

    [Fact]
    public async Task Scatter_read_serializes_a_concurrent_scalar_read_until_all_responses_are_consumed()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var firstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scalarStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithServer(async socket =>
        {
            for (int i = 0; i < 80; i++)
            {
                var request = await Request(socket);
                Assert.Equal(0x10000UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(5)));
                if (i == 0)
                {
                    firstRequest.SetResult();
                    await scalarStarted.Task;
                }
                await Reply(socket, 0, new byte[] { 7 });
            }
            Assert.Equal(0x20000UL, BinaryPrimitives.ReadUInt64LittleEndian((await Request(socket)).AsSpan(5)));
            await Reply(socket, 0, new byte[] { 9 });
        }, helper =>
        {
            var batch = Task.Run(() => helper.ReadMany(Enumerable.Range(0, 80).Select(_ => new MemoryReadRequest(0x10000, new byte[1])).ToArray(), new bool[80]));
            firstRequest.Task.GetAwaiter().GetResult();
            var scalar = Task.Run(() => { scalarStarted.SetResult(); return helper.Read(0x20000, 1); });
            Task.WhenAll(batch, scalar).GetAwaiter().GetResult();
            Assert.Equal(new byte[] { 9 }, scalar.Result);
        });
    }

    [Fact]
    public async Task Partial_scatter_response_closes_connection_and_leaves_later_results_false()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            await Reply(socket, 0, new byte[] { 5 });
            await Request(socket);
            await Send(socket, BitConverter.GetBytes(5));
            socket.Shutdown(SocketShutdown.Both);
        }, helper =>
        {
            var results = new bool[3];
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany(
                [new(0x10000, new byte[1]), new(0x20000, new byte[1]), new(0x30000, new byte[1])], results));
            Assert.Equal(new[] { true, false, false }, results);
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Scatter_reads_coalesced_replies_and_errors_larger_than_the_receive_buffer()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            for (int i = 0; i < 5; i++) await Request(socket);
            byte[][] replies = [Frame(0, [1]), Frame(1, new byte[9000]), Frame(0, new byte[3]), Frame(0, [4]), Frame(0, [5])];
            await Send(socket, replies.SelectMany(x => x).ToArray());
            await Request(socket);
            await Reply(socket, 0, [6]);
        }, helper =>
        {
            var destinations = Enumerable.Range(0, 5).Select(_ => new byte[1]).ToArray();
            var results = new bool[5];
            helper.ReadMany(destinations.Select(d => new MemoryReadRequest(0x10000, d)).ToArray(), results);
            Assert.Equal(new[] { true, false, false, true, true }, results);
            Assert.Equal(new byte[] { 1, 0, 0, 4, 5 }, destinations.Select(x => x[0]));
            Assert.Equal(new byte[] { 6 }, helper.Read(0x10000, 1));
        });
    }

    [Fact]
    public async Task Malformed_first_batch_length_fails_without_waiting_for_the_other_replies()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var observedFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithServer(async socket =>
        {
            await Request(socket);
            var invalid = BitConverter.GetBytes(3);
            foreach (byte value in invalid) await Send(socket, new byte[] { value });
            await observedFailure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }, helper =>
        {
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany(
                [new(0x10000, new byte[1]), new(0x20000, new byte[1])], new bool[2]));
            observedFailure.SetResult();
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Extra_buffered_reply_bytes_cannot_leak_into_the_next_operation()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            await Request(socket);
            await Send(socket, Frame(0, [7]).Concat(new byte[] { 99 }).ToArray());
        }, helper =>
        {
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany([new(0x10000, new byte[1])], new bool[1]));
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Native_batches_chunk_at_1024_and_preserve_mixed_results_and_duplicate_addresses()
    {
        if (!OperatingSystem.IsMacOS()) return;
        const int count = 1030;
        await WithServer(async socket =>
        {
            foreach (int expectedCount in new[] { 1024, 6 })
            {
                var request = await Request(socket, nativeScatter: true);
                Assert.Equal(13, request[0]);
                Assert.Equal(expectedCount, BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(5)));
                Assert.Equal(9 + expectedCount * 12, request.Length);
                await Reply(socket, 0, NativeResults(request), fragmented: true);
            }
        }, helper =>
        {
            var buffers = Enumerable.Range(0, count).Select(_ => new byte[32]).ToArray();
            var requests = buffers.Select((b, i) => new MemoryReadRequest(i == 100 ? 0 : (ulong)(0x10000 + i % 7), b)).ToArray();
            var results = new bool[count];
            helper.ReadMany(requests, results);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(i != 100, results[i]);
                Assert.All(buffers[i], value => Assert.Equal(i == 100 ? 0 : i % 7, value));
            }
        });
    }

    [Fact]
    public async Task Unknown_native_opcode_is_probed_once_then_legacy_transport_is_reused()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            Assert.Equal(13, (await Request(socket, nativeScatter: true))[0]);
            await Reply(socket, 1, [.. BitConverter.GetBytes(14), .. BitConverter.GetBytes(0)]);
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(2, (await Request(socket, nativeScatter: true))[0]);
                await Reply(socket, 0, [11]);
            }
        }, helper =>
        {
            for (int i = 0; i < 2; i++)
            {
                var first = new byte[1]; var second = new byte[1]; var results = new bool[2];
                helper.ReadMany([new(0x10000, first), new(0x10000, second)], results);
                Assert.All(results, Assert.True);
                Assert.Equal(new byte[] { 11 }, first); Assert.Equal(first, second);
            }
        });
    }

    [Fact]
    public async Task Other_native_errors_are_not_silently_retried_as_legacy_reads()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            Assert.Equal(13, (await Request(socket, nativeScatter: true))[0]);
            await Reply(socket, 1, [.. BitConverter.GetBytes(5), .. BitConverter.GetBytes(0)]);
        }, helper =>
        {
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany([new(0x10000, new byte[1])], new bool[1]));
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Fact]
    public async Task Native_batch_holds_the_socket_lock_until_its_complete_response_is_consumed()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var batchReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scalarStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithServer(async socket =>
        {
            var batch = await Request(socket, nativeScatter: true);
            Assert.Equal(13, batch[0]);
            batchReceived.SetResult();
            await scalarStarted.Task;
            await Reply(socket, 0, NativeResults(batch), fragmented: true);
            Assert.Equal(2, (await Request(socket, nativeScatter: true))[0]);
            await Reply(socket, 0, [19]);
        }, helper =>
        {
            var batch = Task.Run(() => helper.ReadMany([new(0x10001, new byte[32]), new(0x10002, new byte[32])], new bool[2]));
            batchReceived.Task.GetAwaiter().GetResult();
            var scalar = Task.Run(() => { scalarStarted.SetResult(); return helper.Read(0x20000, 1); });
            Task.WhenAll(batch, scalar).GetAwaiter().GetResult();
            Assert.Equal(new byte[] { 19 }, scalar.Result);
        });
    }

    [Fact]
    public async Task Incomplete_native_response_closes_without_publishing_earlier_results()
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            var request = await Request(socket, nativeScatter: true);
            var response = Frame(0, NativeResults(request));
            await Send(socket, response.AsMemory(0, response.Length - 1));
            socket.Shutdown(SocketShutdown.Both);
        }, helper =>
        {
            byte[] first = [0xA5], second = [0xA5];
            var results = new bool[2];
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany([new(0x10001, first), new(0x10002, second)], results));
            Assert.Equal(new[] { false, false }, results);
            Assert.Equal(new byte[] { 0xA5 }, first); Assert.Equal(first, second);
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("status")]
    [InlineData("length")]
    [InlineData("trailing")]
    public async Task Malformed_native_results_publish_no_partial_data(string corruption)
    {
        if (!OperatingSystem.IsMacOS()) return;
        await WithServer(async socket =>
        {
            var request = await Request(socket, nativeScatter: true);
            var results = NativeResults(request);
            if (corruption == "count") BinaryPrimitives.WriteInt32LittleEndian(results, 3);
            else if (corruption == "status") BinaryPrimitives.WriteInt32LittleEndian(results.AsSpan(13), 2);
            else if (corruption == "length") BinaryPrimitives.WriteInt32LittleEndian(results.AsSpan(17), 2);
            else results = [.. results, 99];
            await Reply(socket, 0, results, fragmented: true);
        }, helper =>
        {
            byte[] first = [0xA5], second = [0xA5];
            var success = new bool[2];
            Assert.Throws<MemoryAccessException>(() => helper.ReadMany([new(0x10001, first), new(0x10002, second)], success));
            Assert.Equal(new byte[] { 0xA5 }, first); Assert.Equal(first, second);
            Assert.Equal(new[] { false, false }, success);
            Assert.False(helper.TryRead(0x10000, new byte[1]));
        }, expectClose: false);
    }

    static byte[] NativeResults(byte[] request)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(5));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(count);
        for (int i = 0; i < count; i++)
        {
            ulong address = BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(9 + i * 12));
            int length = BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(17 + i * 12));
            Assert.InRange(length, 1, 32);
            writer.Write(address == 0 ? 1 : 0);
            writer.Write(address == 0 ? 0 : length);
            if (address != 0) writer.Write(Enumerable.Repeat((byte)address, length).ToArray());
        }
        return stream.ToArray();
    }

    static async Task WithServer(Func<Socket, Task> exchange, Action<MacHelperProcessMemory> test, bool expectClose = true)
    {
        string dir = Directory.CreateTempSubdirectory("fm27-read-test").FullName;
        string path = Path.Combine(dir, "h.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptAsync();
            Assert.Equal(1, (await Request(socket))[0]);
            await Reply(socket, 0, BitConverter.GetBytes(7));
            await exchange(socket);
            if (expectClose)
            {
                Assert.Equal(9, (await Request(socket))[0]);
                await Reply(socket, 0, []);
            }
        });
        try
        {
            using (var helper = new MacHelperProcessMemory(1234, true, path, MacHelperProcessMemory.ReceiveTimeoutMs)) test(helper);
            await server.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            listener.Close();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    static async Task<byte[]> Request(Socket socket, bool nativeScatter = false)
    {
        var header = new byte[4];
        await Read(socket, header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, 1 << 20);
        var payload = new byte[length];
        await Read(socket, payload);
        if (payload[0] == 13 && !nativeScatter)
        {
            await Reply(socket, 1, [.. BitConverter.GetBytes(14), .. BitConverter.GetBytes(0)]);
            return await Request(socket);
        }
        return payload;
    }

    static async Task Reply(Socket socket, int status, byte[] data, bool fragmented = false)
    {
        var frame = Frame(status, data);
        if (!fragmented) { await Send(socket, frame); return; }
        // Break both the length and status words as well as the payload across writes.
        for (int offset = 0; offset < frame.Length;)
        {
            int count = Math.Min(offset < 12 ? 1 : 17, frame.Length - offset);
            await Send(socket, frame.AsMemory(offset, count));
            offset += count;
        }
    }

    static byte[] Frame(int status, byte[] data)
    {
        var frame = new byte[data.Length + 8];
        BinaryPrimitives.WriteInt32LittleEndian(frame, data.Length + 4);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), status);
        data.CopyTo(frame, 8);
        return frame;
    }

    static async Task Read(Socket socket, Memory<byte> data)
    {
        while (!data.IsEmpty)
        {
            int count = await socket.ReceiveAsync(data, SocketFlags.None);
            if (count == 0) throw new IOException("Peer closed mid-frame");
            data = data[count..];
        }
    }

    static async Task Send(Socket socket, ReadOnlyMemory<byte> data)
    {
        while (!data.IsEmpty)
        {
            int count = await socket.SendAsync(data, SocketFlags.None);
            if (count == 0) throw new IOException("Peer closed mid-frame");
            data = data[count..];
        }
    }
}
