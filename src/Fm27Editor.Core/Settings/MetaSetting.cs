namespace Fm27Editor.Core.Settings;

/// <summary>Whether the main window's META toggle starts enabled on the next launch.</summary>
public static class MetaSetting
{
    static string SettingPath => SettingPathOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fm27Editor", "meta-startup.txt");

    /// <summary>Tests can redirect persistence away from the user's settings.</summary>
    public static string? SettingPathOverride { get; set; }

    public static bool EnableAtStartup { get; private set; }

    public static void Load()
    {
        EnableAtStartup = false;
        try
        {
            EnableAtStartup = File.Exists(SettingPath) && File.ReadAllText(SettingPath).Trim() == "1";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void SetEnableAtStartup(bool enabled)
    {
        EnableAtStartup = enabled;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath)!);
            File.WriteAllText(SettingPath, enabled ? "1" : "0");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
