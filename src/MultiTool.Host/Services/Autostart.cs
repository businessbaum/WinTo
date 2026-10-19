using Microsoft.Win32;

namespace MultiTool.Host.Services;

/// <summary>
/// The per-user Run entry. Same value name as the installer, so both switch the same thing.
/// </summary>
/// <remarks>The value was called "MultiTool" until 0.13.0; setting either state removes that one.</remarks>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinTo";
    private const string FormerValueName = "MultiTool";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string { Length: > 0 };
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.DeleteValue(FormerValueName, throwOnMissingValue: false);

            if (enabled)
            {
                if (Environment.ProcessPath is not { } exe) return "Program path unknown.";
                key.SetValue(ValueName, $"\"{exe}\" --background", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
