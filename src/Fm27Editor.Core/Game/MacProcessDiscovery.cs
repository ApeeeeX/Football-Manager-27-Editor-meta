using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Game;

/// <summary>Finds running Football Manager 27 instances on macOS and where game_plugin is loaded.</summary>
public static partial class MacProcessDiscovery
{
    const string LibSystem = "/usr/lib/libSystem.B.dylib";
    const string ExecutableName = "Football Manager 27";
    const string TouchExecutableName = "Football Manager Touch";
    const string AlternateExecutableName = "fm";
    const string DefaultModule = "game_plugin";
    const int MaxProcessCount = 16384, MaxImageCount = 8192, MaxPathLength = 4096;
    const int DyldImageInfoStride = 24, MachHeader64Size = 32;
    const uint MachMagic64 = 0xFEEDFACF, LcSegment64 = 0x19;
    const int MaxLoadCommands = 16 * 1024 * 1024;

    /// <summary>Every running Football Manager 27 process with <paramref name="moduleName"/> loaded.</summary>
    public static IReadOnlyList<GameProcess> LocateAll(string moduleName = DefaultModule)
    {
        string module = StripExtension(moduleName);
        var candidates = ListCandidates();
        if (candidates.Count == 0) throw new GameNotRunningException($"{ExecutableName} is not running");

        var games = new List<GameProcess>();
        Exception? error = null;
        foreach (var (pid, exePath) in candidates)
        {
            IMacMemory? memory = null;
            try
            {
                memory = MacMemory.Open(pid, writable: false);
                var image = FindImage(memory, module);
                if (image is null) continue;
                ulong span = MachOSpan(memory, image.Value.Address);
                games.Add(new GameProcess(pid, image.Value.Address, checked((int)span), null, exePath));
            }
            catch (Exception ex) when (ex is MemoryAccessException or OverflowException)
            {
                error = ex;
            }
            finally { memory?.Dispose(); }
        }

        if (games.Count == 0)
            throw new GameNotRunningException(error is null
                ? $"{moduleName} is not loaded yet"
                : $"Cannot inspect {ExecutableName}: {Explain(error.Message)}");
        return games;
    }

    // KERN_FAILURE from task_for_pid, even for the root helper: the game uses the Hardened Runtime, and System Integrity
    // Protection's debugging restrictions refuse access to such a process. Measured: access works with
    // "Debugging Restrictions: disabled" in `csrutil status` and fails with code 5 when they are on.
    internal static string Explain(string message) =>
        System.Text.RegularExpressions.Regex.IsMatch(message, @"task_for_pid\(\d+\) failed with kern_return_t 5\b")
            ? message + ". macOS blocks direct access to the Steam copy of the game while System Integrity Protection is on. " +
              "Set up the editor plugin, then start the game again from Steam."
            : message;

    /// <summary>Processes whose executable name is Football Manager 27 (or fm), with their executable paths.</summary>
    static unsafe List<(int Pid, string Path)> ListCandidates()
    {
        var candidates = new List<(int, string)>();
        int capacity = Math.Max(proc_listallpids(null, 0), MaxProcessCount);
        var pids = new int[capacity];
        int count;
        fixed (int* p = pids) count = proc_listallpids(p, capacity * sizeof(int));
        if (count <= 0) return candidates;
        count = Math.Min(count, capacity);

        var pathBuffer = new byte[MaxPathLength];
        for (int i = 0; i < count; i++)
        {
            int pid = pids[i];
            if (pid <= 0) continue;
            int length;
            fixed (byte* b = pathBuffer) length = proc_pidpath(pid, b, (uint)pathBuffer.Length);
            if (length <= 0) continue;
            string path = Encoding.UTF8.GetString(pathBuffer, 0, Math.Min(length, pathBuffer.Length));
            if (IsGameExecutable(path)) candidates.Add((pid, path));
        }
        return candidates;
    }

    static bool IsGameExecutable(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)
               || name.Equals(TouchExecutableName, StringComparison.OrdinalIgnoreCase)
               || name.Equals(AlternateExecutableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The loaded image matching <paramref name="module"/> from the task's dyld image table.</summary>
    static (ulong Address, string Path)? FindImage(IMacMemory memory, string module)
    {
        if (!memory.TryGetTaskDyldInfo(out ulong infoAddress, out _)) return null;
        var header = new byte[16];
        if (!memory.TryRead(infoAddress, header)) return null;
        uint rawCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        ulong array = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        if (rawCount == 0 || rawCount > MaxImageCount || array == 0) return null;

        int count = (int)rawCount;
        var entries = new byte[count * DyldImageInfoStride];
        if (!memory.TryRead(array, entries)) return null;
        for (int i = 0; i < count; i++)
        {
            var entry = entries.AsSpan(i * DyldImageInfoStride, DyldImageInfoStride);
            ulong loadAddress = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            ulong pathAddress = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
            if (loadAddress == 0 || pathAddress == 0) continue;
            string? path = memory.TryReadCString(pathAddress);
            if (path is null || !MatchesModule(path, module)) continue;
            return (loadAddress, path);
        }
        return null;
    }

    static bool MatchesModule(string path, string module) =>
        Path.GetFileName(path).Equals(module, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith($"{module}.bundle/Contents/MacOS/{module}", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith($"/{module}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Total vm span of the image's LC_SEGMENT_64 commands, with __PAGEZERO excluded.</summary>
    static ulong MachOSpan(IMacMemory memory, ulong loadAddress)
    {
        var header = new byte[MachHeader64Size];
        if (!memory.TryRead(loadAddress, header))
            throw new MemoryAccessException($"Cannot read the Mach-O header at 0x{loadAddress:X}");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != MachMagic64)
            throw new MemoryAccessException($"0x{loadAddress:X} does not hold a 64-bit Mach-O header");

        uint commandCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint commandsSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        if (commandsSize == 0 || commandsSize > MaxLoadCommands)
            throw new MemoryAccessException($"Invalid Mach-O load command size {commandsSize} at 0x{loadAddress:X}");
        byte[] commands = memory.Read(loadAddress + MachHeader64Size, (int)commandsSize);

        ulong minStart = ulong.MaxValue, maxEnd = 0;
        int offset = 0;
        for (uint i = 0; i < commandCount && offset + 8 <= commands.Length; i++)
        {
            uint command = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(offset));
            uint commandSize = BinaryPrimitives.ReadUInt32LittleEndian(commands.AsSpan(offset + 4));
            if (commandSize < 8 || offset + (long)commandSize > commands.Length) break;
            if (command == LcSegment64 && commandSize >= 72 && !IsPageZero(commands.AsSpan(offset + 8, 16)))
            {
                ulong vmAddress = BinaryPrimitives.ReadUInt64LittleEndian(commands.AsSpan(offset + 24));
                ulong vmSize = BinaryPrimitives.ReadUInt64LittleEndian(commands.AsSpan(offset + 32));
                if (vmSize > 0)
                {
                    minStart = Math.Min(minStart, vmAddress);
                    maxEnd = Math.Max(maxEnd, vmAddress + vmSize);
                }
            }
            offset += (int)commandSize;
        }
        if (maxEnd <= minStart) throw new MemoryAccessException($"No segments in the image at 0x{loadAddress:X}");
        return maxEnd - minStart;
    }

    static bool IsPageZero(ReadOnlySpan<byte> segmentName)
    {
        int length = segmentName.IndexOf((byte)0);
        if (length < 0) length = segmentName.Length;
        return segmentName[..length].SequenceEqual("__PAGEZERO"u8);
    }

    static string StripExtension(string moduleName) =>
        moduleName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? moduleName[..^4] : moduleName;

    [LibraryImport(LibSystem)]
    private static unsafe partial int proc_listallpids(int* buffer, int bufferSize);

    [LibraryImport(LibSystem)]
    private static unsafe partial int proc_pidpath(int pid, byte* buffer, uint bufferSize);
}
