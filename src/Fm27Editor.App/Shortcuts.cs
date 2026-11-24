using Avalonia.Input;

namespace Fm27Editor.App;

/// <summary>The editor's keyboard shortcuts use Ctrl on Windows and Command on macOS, and their labels follow.</summary>
internal static class Shortcuts
{
    public static KeyModifiers Command => CommandFor(OperatingSystem.IsMacOS());

    public static KeyModifiers CommandFor(bool mac) => mac ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>True when the platform's command key is held, with or without other modifiers.</summary>
    public static bool Holds(KeyModifiers modifiers) => modifiers.HasFlag(Command);

    /// <summary>True when only the platform's command key is held.</summary>
    public static bool Only(KeyModifiers modifiers) => modifiers == Command;

    /// <summary>Shows "Ctrl+S" as "⌘S" on macOS.</summary>
    public static string Label(string text) => LabelFor(text, OperatingSystem.IsMacOS());

    public static string LabelFor(string text, bool mac) => mac ? text.Replace("Ctrl+", "⌘", StringComparison.Ordinal) : text;
}
