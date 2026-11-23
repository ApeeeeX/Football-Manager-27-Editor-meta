namespace Fm27Editor.Core.Memory;

/// <summary>
/// A macOS process's memory, either directly through Mach VM (<see cref="MacProcessMemory"/>) or through the
/// privileged on-demand helper (<see cref="MacHelperProcessMemory"/>). Callers only need the dyld lookup and the
/// raw string read that process discovery uses.
/// </summary>
public interface IMacMemory : IProcessMemory, IDisposable
{
    /// <summary>The task's dyld_all_image_infos address and size; false when task_info cannot provide them.</summary>
    bool TryGetTaskDyldInfo(out ulong allImageInfoAddress, out ulong allImageInfoSize);

    /// <summary>Reads a NUL-terminated UTF-8 string, for dyld paths and other remote strings.</summary>
    string? TryReadCString(ulong address, int maxLength = 1024);
}

/// <summary>Chooses direct Mach access or the privileged helper for a macOS process.</summary>
public static class MacMemory
{
    /// <summary>
    /// Opens <paramref name="processId"/> with Mach VM when this process is allowed to, otherwise through the
    /// installed privileged helper. Throws <see cref="MemoryAccessException"/> when neither path can attach.
    /// </summary>
    public static IMacMemory Open(int processId, bool writable)
    {
        // The in-game plugin needs neither root nor task_for_pid, so it works with System Integrity Protection on.
        string plugin = PluginSocketPath(processId);
        if (File.Exists(plugin))
        {
            try { return new MacHelperProcessMemory(processId, writable, plugin, MacHelperProcessMemory.ReceiveTimeoutMs); }
            catch (Exception ex) when (ex is MemoryAccessException or System.Net.Sockets.SocketException) { }
        }
        try
        {
            return new MacProcessMemory(processId, writable);
        }
        catch (MemoryAccessException) when (MacHelperProcessMemory.Available)
        {
            return new MacHelperProcessMemory(processId, writable);
        }
    }

    /// <summary>Where libfm27editor_plugin.dylib listens inside the game: the user's temporary folder, per process.</summary>
    public static string PluginSocketPath(int processId) => Path.Combine(Path.GetTempPath(), $"fm27editor-{processId}.sock");
}