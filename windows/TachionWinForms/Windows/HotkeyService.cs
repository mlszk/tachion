using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Tachion.Windows;

public readonly record struct HotkeyDefinition(Keys Key, uint Modifiers, string Text);

public static class HotkeyService
{
    public const int WmHotkey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public static bool Register(IntPtr handle, int id, HotkeyDefinition hotkey, out string error)
    {
        Unregister(handle, id);
        if (!RegisterHotKey(handle, id, hotkey.Modifiers | ModNoRepeat, (uint)hotkey.Key))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        error = "";
        return true;
    }

    public static void Unregister(IntPtr handle, int id)
    {
        if (handle == IntPtr.Zero) return;
        try { UnregisterHotKey(handle, id); } catch { }
    }

    public static bool TryParse(string? text, out HotkeyDefinition hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = 0u;
        var key = Keys.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Trim();
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
            }
            else if (TryParseKey(part, out var parsedKey))
            {
                key = parsedKey;
            }
            else
            {
                return false;
            }
        }

        if (modifiers == 0 || key == Keys.None || IsModifierOnlyKey(key)) return false;
        hotkey = new HotkeyDefinition(key, modifiers, ToDisplayText(key, modifiers));
        return true;
    }

    public static bool TryFromKeyData(Keys keyData, out HotkeyDefinition hotkey)
    {
        hotkey = default;
        var key = keyData & Keys.KeyCode;
        if (key == Keys.None || IsModifierOnlyKey(key)) return false;

        var modifiers = 0u;
        if ((keyData & Keys.Control) == Keys.Control) modifiers |= ModControl;
        if ((keyData & Keys.Alt) == Keys.Alt) modifiers |= ModAlt;
        if ((keyData & Keys.Shift) == Keys.Shift) modifiers |= ModShift;

        // Require at least one modifier so a plain letter/function key is never stolen globally.
        if (modifiers == 0) return false;

        hotkey = new HotkeyDefinition(key, modifiers, ToDisplayText(key, modifiers));
        return true;
    }

    private static bool TryParseKey(string text, out Keys key)
    {
        key = Keys.None;
        if (text.Length == 1 && char.IsDigit(text[0]))
        {
            key = (Keys)((int)Keys.D0 + (text[0] - '0'));
            return true;
        }

        if (Enum.TryParse(text, ignoreCase: true, out key))
            return true;

        if (text.Length == 1 && char.IsLetter(text[0]))
        {
            key = Enum.Parse<Keys>(text.ToUpperInvariant());
            return true;
        }

        return false;
    }

    private static bool IsModifierOnlyKey(Keys key)
    {
        return key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
            or Keys.Control or Keys.Shift or Keys.Alt;
    }

    private static string ToDisplayText(Keys key, uint modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyToText(key));
        return string.Join("+", parts);
    }

    private static string KeyToText(Keys key)
    {
        if (key >= Keys.D0 && key <= Keys.D9)
            return ((char)('0' + ((int)key - (int)Keys.D0))).ToString();
        return key.ToString();
    }
}
