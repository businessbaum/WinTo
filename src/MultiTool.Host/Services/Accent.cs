using Avalonia.Media;
using Microsoft.Win32;
using MultiTool.UI;

namespace MultiTool.Host.Services;

/// <summary>The accent setting: presets, the Windows color, and applying it (<see cref="AccentTheme"/>).</summary>
internal static class Accent
{
    /// <summary>Stored for "follow the Windows accent color".</summary>
    public const string Windows = "windows";

    /// <summary>
    /// Mid-tones that read on the dark background. Amber is light enough that text on it turns dark
    /// (<see cref="AccentTheme.Luminance"/>).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Presets =
    [
        ("Violet", "#7C5CFF"),
        ("Blue", "#2F80ED"),
        ("Teal", "#14A39A"),
        ("Green", "#2EA662"),
        ("Amber", "#E8A317"),
        ("Orange", "#F06A2A"),
        ("Red", "#E5484D"),
        ("Pink", "#E0479E"),
    ];

    public static Color Resolve(string setting) =>
        setting == Windows ? WindowsColor() ?? AccentTheme.Default : AccentTheme.Parse(setting);

    public static void Apply(string setting) => AccentTheme.Apply(Resolve(setting));

    /// <summary>The Windows accent color, or <c>null</c> if it can't be read.</summary>
    /// <remarks>
    /// <c>HKCU\Software\Microsoft\Windows\DWM\AccentColor</c> is a DWORD in ABGR order, the color Windows uses
    /// for title bars and the Start menu. Read when applied; a change in Windows shows at the next start or
    /// when the setting is chosen again.
    /// </remarks>
    public static Color? WindowsColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is not int value) return null;

            var abgr = unchecked((uint)value);
            return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
