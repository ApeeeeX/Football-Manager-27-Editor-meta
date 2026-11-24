using System.Buffers.Binary;
using System.Net.Sockets;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Tests;

public class MacHelperProcessMemoryTests
{
    [Fact]
    public async Task Failed_open_closes_socket_so_a_single_client_helper_does_not_wedge()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string dir = Directory.CreateTempSubdirectory("fm27-helper-test").FullName;
        string sockPath = Path.Combine(dir, "h.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(sockPath));
        listener.Listen(8);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(cts.Token); }
                catch (OperationCanceledException) { break; }

                using (client)
                {
                    try
                    {
                        var lenBuf = new byte[4];
                        while (true)
                        {
                            int read = await ReadExactAsync(client, lenBuf, cts.Token);
                            if (read == 0) break;
                            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                            var req = new byte[len];
                            if (await ReadExactAsync(client, req, cts.Token) == 0) break;

                            if (req[0] == 1) // OP_OPEN -> status=0, handle=1
                            {
                                var resp = new byte[12];
                                BinaryPrimitives.WriteUInt32LittleEndian(resp, 8);
                                BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(8), 1);
                                await client.SendAsync(resp, SocketFlags.None, cts.Token);
                            }
                            else if (req[0] == 9) // OP_CLOSE -> status=0
                            {
                                var resp = new byte[8];
                                BinaryPrimitives.WriteUInt32LittleEndian(resp, 4);
                                BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                await client.SendAsync(resp, SocketFlags.None, cts.Token);
                            }
                        }
                    }
                    catch (SocketException) { }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, cts.Token);

        try
        {
            var first = new MacHelperProcessMemory(1234, writable: false, sockPath, timeoutMs: MacHelperProcessMemory.ReceiveTimeoutMs);
            Assert.ThrowsAny<Exception>(() => new MacHelperProcessMemory(1234, writable: true, sockPath, timeoutMs: 200));

            first.Dispose();

            using var third = new MacHelperProcessMemory(1234, writable: true, sockPath, timeoutMs: MacHelperProcessMemory.ReceiveTimeoutMs);
            Assert.True(third.CanWrite);
        }
        finally
        {
            cts.Cancel();
            try { await server; } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Code_patch_host_translates_protections_commits_executable_and_frees_tracked_bytes()
    {
        if (!OperatingSystem.IsMacOS()) return;

        string dir = Directory.CreateTempSubdirectory("fm27-helper-prot").FullName;
        string sockPath = Path.Combine(dir, "h.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(sockPath));
        listener.Listen(4);

        var protects = new List<(ulong Address, uint Bytes, uint Prot, byte Copy)>();
        ulong freedBytes = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(cts.Token); }
                catch (OperationCanceledException) { break; }

                using (client)
                {
                    try
                    {
                        var lenBuf = new byte[4];
                        while (true)
                        {
                            int read = await ReadExactAsync(client, lenBuf, cts.Token);
                            if (read == 0) break;
                            uint len = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
                            var req = new byte[len];
                            if (await ReadExactAsync(client, req, cts.Token) == 0) break;

                            switch (req[0])
                            {
                                case 1: // OP_OPEN
                                {
                                    var resp = new byte[12];
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp, 8);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(8), 1);
                                    await client.SendAsync(resp, SocketFlags.None, cts.Token);
                                    break;
                                }
                                case 5: // OP_ALLOCATE_NEAR
                                {
                                    var resp = new byte[16];
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp, 12);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                    BinaryPrimitives.WriteUInt64LittleEndian(resp.AsSpan(8), 0x1_6000_0000UL);
                                    await client.SendAsync(resp, SocketFlags.None, cts.Token);
                                    break;
                                }
                                case 6: // OP_PROTECT
                                {
                                    protects.Add((
                                        BinaryPrimitives.ReadUInt64LittleEndian(req.AsSpan(5)),
                                        BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(13)),
                                        BinaryPrimitives.ReadUInt32LittleEndian(req.AsSpan(17)),
                                        req[21]));
                                    var resp = new byte[12];
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp, 8);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp.AsSpan(8), 0x20); // PAGE_EXECUTE_READ
                                    await client.SendAsync(resp, SocketFlags.None, cts.Token);
                                    break;
                                }
                                case 7: // OP_FREE
                                {
                                    freedBytes = BinaryPrimitives.ReadUInt64LittleEndian(req.AsSpan(13));
                                    var resp = new byte[8];
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp, 4);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                    await client.SendAsync(resp, SocketFlags.None, cts.Token);
                                    break;
                                }
                                case 9: // OP_CLOSE
                                {
                                    var resp = new byte[8];
                                    BinaryPrimitives.WriteUInt32LittleEndian(resp, 4);
                                    BinaryPrimitives.WriteInt32LittleEndian(resp.AsSpan(4), 0);
                                    await client.SendAsync(resp, SocketFlags.None, cts.Token);
                                    break;
                                }
                            }
                        }
                    }
                    catch (SocketException) { }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, cts.Token);

        try
        {
            using var helper = new MacHelperProcessMemory(1234, writable: true, sockPath, timeoutMs: MacHelperProcessMemory.ReceiveTimeoutMs);
            var patchHost = (Fm27Editor.Core.Presets.ICodePatchHost)helper;
            ulong cave = patchHost.AllocateNear(0x1_5F00_0000UL, 0x14000);
            Assert.Equal(0x1_6000_0000UL, cave);

            patchHost.CommitExecutable(cave + 0x10000, 0x4000);
            uint prev = patchHost.Protect(0x1_5F00_1000UL, 8, 0x40); // PAGE_EXECUTE_READWRITE -> VM_PROT_R|W|COPY (0x13)
            Assert.Equal(0x20u, prev);
            patchHost.Protect(0x1_5F00_1000UL, 8, prev);              // PAGE_EXECUTE_READ -> VM_PROT_R|X (0x05)
            patchHost.Protect(cave, 0x1000, 0x04);                    // PAGE_READWRITE -> VM_PROT_R|W (0x03)
            patchHost.Free(cave);

            Assert.Equal(4, protects.Count);
            Assert.Equal((cave + 0x10000, 0x4000u, 0x05u, (byte)0), protects[0]);
            Assert.Equal((0x1_5F00_1000UL, 8u, 0x13u, (byte)0), protects[1]);
            Assert.Equal((0x1_5F00_1000UL, 8u, 0x05u, (byte)0), protects[2]);
            Assert.Equal((cave, 0x1000u, 0x03u, (byte)0), protects[3]);
            Assert.Equal(0x14000UL, freedBytes);
        }
        finally
        {
            cts.Cancel();
            try { await server; } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static async Task<int> ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int got = await socket.ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None, ct);
            if (got == 0) return 0;
            offset += got;
        }
        return offset;
    }
}
