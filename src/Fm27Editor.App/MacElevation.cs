using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Fm27Editor.App;

/// <summary>
/// Installs the privileged, on-demand FM27 helper the first time it is needed. Later launches find the helper,
/// its launchd plist and the socket already in place and return without asking for a password.
/// </summary>
internal static partial class MacElevation
{
    const string HelperName = "Fm27EditorHelper";
    const string Destination = "/Library/PrivilegedHelperTools/Fm27EditorHelper";
    const string Plist = "/Library/LaunchDaemons/com.fm27editor.helper.plist";
    const string Label = "com.fm27editor.helper";
    const string SocketPath = "/var/run/fm27editor.sock";

    /// <summary>
    /// Returns true when the helper is installed and its socket exists. Only the first install (or an update)
    /// runs <c>osascript ... with administrator privileges</c>; every later call returns without a prompt.
    /// </summary>
    public static bool EnsureHelperInstalled()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (geteuid() == 0) return true;

        string bundled = Path.Combine(AppContext.BaseDirectory, HelperName);
        if (!File.Exists(bundled)) return false;

        if (IsInstalled(bundled)) return true;

        try
        {
            return Install(bundled);
        }
        catch (Exception)
        {
            return false;
        }
    }

    static bool IsInstalled(string bundled)
    {
        if (!File.Exists(Destination) || !File.Exists(Plist) || !File.Exists(SocketPath)) return false;

        try
        {
            string text = File.ReadAllText(Plist);
            if (!text.Contains("Fm27Socket", StringComparison.Ordinal) ||
                !text.Contains(SocketPath, StringComparison.Ordinal) ||
                !text.Contains("<false/>", StringComparison.Ordinal))
                return false;
        }
        catch
        {
            return false;
        }
        // A version marker, not the bytes: a rebuild of the same helper on another machine must not ask for the password again.
        return HelperVersion(bundled) is { } version ? HelperVersion(Destination) == version : SameContent(bundled, Destination);
    }

    const string VersionMarker = "FM27_HELPER_VERSION=";

    /// <summary>The number in the helper's "FM27_HELPER_VERSION=n;" marker; null when the file has none.</summary>
    internal static int? HelperVersion(string path)
    {
        try { return HelperVersion(File.ReadAllBytes(path)); }
        catch { return null; }
    }

    internal static int? HelperVersion(ReadOnlySpan<byte> bytes)
    {
        int at = bytes.IndexOf(Encoding.ASCII.GetBytes(VersionMarker));
        if (at < 0) return null;
        var rest = bytes[(at + VersionMarker.Length)..];
        int end = rest.IndexOf((byte)';');
        return end > 0 && int.TryParse(Encoding.ASCII.GetString(rest[..end]), out int version) ? version : null;
    }

    static bool SameContent(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;
            using Stream leftStream = File.OpenRead(left);
            using Stream rightStream = File.OpenRead(right);
            return SHA256.HashData(leftStream).AsSpan().SequenceEqual(SHA256.HashData(rightStream));
        }
        catch
        {
            return false;
        }
    }

    static bool Install(string bundled)
    {
        string temporary = Path.Combine(Path.GetTempPath(), $"com.fm27editor.helper.{Environment.ProcessId}.plist");
        try
        {
            File.WriteAllText(temporary, PlistContents);

            string script =
                "launchctl bootout system/" + Label + " 2>/dev/null || true; " +
                "mkdir -p /Library/PrivilegedHelperTools; " +
                "install -m 755 -o root -g wheel " + EscapeForShell(bundled) + " " + EscapeForShell(Destination) + "; " +
                "install -m 644 -o root -g wheel " + EscapeForShell(temporary) + " " + EscapeForShell(Plist) + "; " +
                "rm -f " + EscapeForShell(SocketPath) + "; " +
                "launchctl bootstrap system " + EscapeForShell(Plist);

            string appleScript = "do shell script \"" + EscapeForAppleScript(script) + "\" with administrator privileges";

            var start = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(appleScript);

            using var process = Process.Start(start);
            if (process is null) return false;
            process.WaitForExit();
            if (process.ExitCode != 0) return false;
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }

        WaitForSocket();
        return true;
    }

    static void WaitForSocket()
    {
        for (int i = 0; i < 200; i++)
        {
            if (File.Exists(SocketPath)) return;
            Thread.Sleep(50);
        }
    }

    static string EscapeForShell(string word) => "'" + word.Replace("'", "'\\''") + "'";

    static string EscapeForAppleScript(string commandLine) =>
        commandLine.Replace("\\", "\\\\").Replace("\"", "\\\"");

    [LibraryImport("libSystem.B.dylib")]
    private static partial uint geteuid();

    static string PlistContents =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\">\n" +
        "<dict>\n" +
        "    <key>Label</key>\n" +
        $"    <string>{Label}</string>\n" +
        "    <key>ProgramArguments</key>\n" +
        "    <array>\n" +
        $"        <string>{Destination}</string>\n" +
        "    </array>\n" +
        "    <key>Sockets</key>\n" +
        "    <dict>\n" +
        "        <key>Fm27Socket</key>\n" +
        "        <dict>\n" +
        "            <key>SockPathName</key>\n" +
        $"            <string>{SocketPath}</string>\n" +
        "            <key>SockPathMode</key>\n" +
        "            <integer>438</integer>\n" +
        "            <key>SockPathOwner</key>\n" +
        "            <integer>0</integer>\n" +
        "            <key>SockPathGroup</key>\n" +
        "            <integer>0</integer>\n" +
        "        </dict>\n" +
        "    </dict>\n" +
        "    <key>RunAtLoad</key>\n" +
        "    <false/>\n" +
        "    <key>KeepAlive</key>\n" +
        "    <false/>\n" +
        "    <key>ProcessType</key>\n" +
        "    <string>Interactive</string>\n" +
        "</dict>\n" +
        "</plist>\n";
}