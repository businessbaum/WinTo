using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;

namespace MultiTool.Modules.Soundboard.Models;

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyModifiers>))]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A globally registered key combination. Defined by Win32 virtual key, not Avalonia's <c>Key</c>: the
/// low-level hook delivers VKs, and only those let Windows report layout-correct names.
/// </summary>
public sealed class Hotkey : IEquatable<Hotkey>
{
    public int VirtualKey { get; set; }

    public HotkeyModifiers Modifiers { get; set; }

    [JsonIgnore]
    public bool IsEmpty => VirtualKey == 0;

    [JsonIgnore]
    public string DisplayName => IsEmpty ? "—" : KeyNames.Format(this);

    public bool Equals(Hotkey? other) =>
        other is not null && other.VirtualKey == VirtualKey && other.Modifiers == Modifiers;

    public override bool Equals(object? obj) => Equals(obj as Hotkey);

    public override int GetHashCode() => HashCode.Combine(VirtualKey, (int)Modifiers);

    public Hotkey Clone() => new() { VirtualKey = VirtualKey, Modifiers = Modifiers };
}

/// <summary>
/// Translates virtual keys into display names via <c>GetKeyNameText</c>, so names match the actual keyboard
/// layout (Y/Z, umlauts, ...).
/// </summary>
internal static class KeyNames
{
    private const uint MAPVK_VK_TO_VSC = 0;

    // Keys whose scan code needs the extended bit, or Windows returns the wrong name
    // (e.g. "Num 4" instead of "Left").
    private static readonly HashSet<int> ExtendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp/PgDn/End/Home/arrows
        0x2C, 0x2D, 0x2E,                               // PrtScn/Ins/Del
        0x90, 0x6F, 0xA3, 0xA5,                         // NumLock, Num /, RCtrl, RAlt
    ];

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetKeyNameTextW")]
    private static extern int GetKeyNameText(int lParam, StringBuilder lpString, int cchSize);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(4);
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(hotkey.VirtualKey));
        return string.Join(" + ", parts);
    }

    public static string KeyName(int virtualKey)
    {
        if (virtualKey == 0) return "—";

        try
        {
            var scanCode = MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC);
            if (scanCode != 0)
            {
                var lParam = (int)(scanCode << 16);
                if (ExtendedKeys.Contains(virtualKey)) lParam |= 1 << 24;

                var buffer = new StringBuilder(64);
                if (GetKeyNameText(lParam, buffer, buffer.Capacity) > 0)
                {
                    var name = buffer.ToString().Trim();
                    if (name.Length > 0) return Capitalize(name);
                }
            }
        }
        catch (Exception)
        {
            // Falls back to the table below.
        }

        return Fallback(virtualKey);
    }

    private static string Capitalize(string value) =>
        value.Length > 1 && value.ToUpperInvariant() == value
            ? value[0] + value[1..].ToLowerInvariant()
            : value;

    private static string Fallback(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),                 // 0-9
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),                 // A-Z
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),                     // F1-F24
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),                  // Numpad 0-9
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Esc",
        0x20 => "Space",
        _ => $"VK 0x{vk:X2}",
    };
}
