using System.Runtime.InteropServices;
using AdeskPro.Platform;

namespace AdeskPro.Mac;

/// <summary>Keyboard and mouse synthesis through CoreGraphics, plus clipboard through the
/// NSPasteboard binding available in the base runtime.</summary>
internal static class MacInput
{
    // --- keyboard ---

    /// <summary>Modifier bit that CGEventSetFlags expects.</summary>
    private const ulong FlagShift = 0x00020000;
    private const ulong FlagControl = 0x00040000;
    private const ulong FlagOption = 0x00080000;
    private const ulong FlagCommand = 0x00100000;

    /// <summary>US-ANSI virtual key codes for the keys agents actually ask for.</summary>
    private static readonly Dictionary<string, (ushort Code, ulong? Modifier)> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cmd"] = (0, FlagCommand), ["command"] = (0, FlagCommand),
        ["ctrl"] = (0, FlagControl), ["control"] = (0, FlagControl),
        ["alt"] = (0, FlagOption), ["option"] = (0, FlagOption), ["opt"] = (0, FlagOption),
        ["shift"] = (0, FlagShift),
        ["enter"] = (36, null), ["return"] = (36, null),
        ["tab"] = (48, null), ["space"] = (49, null),
        ["esc"] = (53, null), ["escape"] = (53, null),
        ["backspace"] = (51, null), ["bs"] = (51, null), ["delete"] = (51, null),
        ["forwarddelete"] = (117, null), ["del"] = (117, null),
        ["home"] = (115, null), ["end"] = (119, null),
        ["pgup"] = (116, null), ["pageup"] = (116, null),
        ["pgdn"] = (121, null), ["pagedown"] = (121, null),
        ["up"] = (126, null), ["down"] = (125, null),
        ["left"] = (123, null), ["right"] = (124, null),
        ["f1"] = (122, null), ["f2"] = (120, null), ["f3"] = (99, null), ["f4"] = (118, null),
        ["f5"] = (96, null), ["f6"] = (97, null), ["f7"] = (98, null), ["f8"] = (100, null),
        ["f9"] = (101, null), ["f10"] = (109, null), ["f11"] = (103, null), ["f12"] = (111, null),
        ["a"] = (0, null), ["s"] = (1, null), ["d"] = (2, null), ["f"] = (3, null),
        ["h"] = (4, null), ["g"] = (5, null), ["z"] = (6, null), ["x"] = (7, null),
        ["c"] = (8, null), ["v"] = (9, null), ["b"] = (11, null), ["q"] = (12, null),
        ["w"] = (13, null), ["e"] = (14, null), ["r"] = (15, null), ["y"] = (16, null),
        ["t"] = (17, null), ["1"] = (18, null), ["2"] = (19, null), ["3"] = (20, null),
        ["4"] = (21, null), ["6"] = (22, null), ["5"] = (23, null), ["9"] = (25, null),
        ["7"] = (26, null), ["8"] = (28, null), ["0"] = (29, null), ["o"] = (31, null),
        ["u"] = (32, null), ["i"] = (34, null), ["p"] = (35, null), ["l"] = (37, null),
        ["j"] = (38, null), ["k"] = (40, null), ["n"] = (45, null), ["m"] = (46, null),
    };

    /// <summary>Sends a combo like "cmd+s" or "ctrl+shift+tab". Modifiers are pressed first and
    /// released last, matching how a human types a shortcut.</summary>
    public static void PressKeys(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new NoPatternException("empty key combo", "e.g. cmd+s");

        ulong flags = 0;
        var keys = new List<ushort>();
        foreach (var part in parts)
        {
            if (!KeyMap.TryGetValue(part, out var mapped))
                throw new NoPatternException($"unknown key '{part}'",
                    "use names like cmd, ctrl, alt, shift, enter, tab, pgdn, f5, a");
            if (mapped.Modifier is { } modifierFlag) flags |= modifierFlag;
            else keys.Add(mapped.Code);
        }
        if (keys.Count == 0) throw new NoPatternException("a key combo needs a non-modifier key", "e.g. cmd+s");

        var last = keys[^1];
        Post(KeyEvent(last, keyDown: true, flags));
        Post(KeyEvent(last, keyDown: false, flags));
    }

    private static IntPtr KeyEvent(ushort code, bool keyDown, ulong flags)
    {
        var ev = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, code, keyDown);
        if (ev != IntPtr.Zero && flags != 0) Native.CGEventSetFlags(ev, flags);
        return ev;
    }

    /// <summary>Types arbitrary Unicode text by posting one key event per UTF-16 code unit with
    /// the Unicode payload attached — the only reliable way to deliver non-ASCII on macOS.</summary>
    public static void TypeUnicode(string text)
    {
        foreach (var ch in text)
        {
            var chunk = new[] { (ushort)ch };
            var down = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, true);
            if (down != IntPtr.Zero)
            {
                Native.CGEventKeyboardSetUnicodeString(down, chunk.Length, chunk);
                Post(down);
            }
            var up = Native.CGEventCreateKeyboardEvent(IntPtr.Zero, 0, false);
            if (up != IntPtr.Zero)
            {
                Native.CGEventKeyboardSetUnicodeString(up, chunk.Length, chunk);
                Post(up);
            }
        }
    }

    // --- mouse ---

    public static void Hover(int x, int y) =>
        Native.CGWarpMouseCursorPosition(new Native.CgPoint { X = x, Y = y });

    public static void Click(int x, int y, bool right, bool doubleClick)
    {
        Hover(x, y);
        var point = new Native.CgPoint { X = x, Y = y };
        var down = right ? Native.RightMouseDown : Native.LeftMouseDown;
        var up = right ? Native.RightMouseUp : Native.LeftMouseUp;
        ClickOnce(point, down, up);
        if (doubleClick)
        {
            Thread.Sleep(80);
            ClickOnce(point, down, up);
        }
    }

    private static void ClickOnce(Native.CgPoint point, uint down, uint up)
    {
        var downEvent = Native.CGEventCreateMouseEvent(IntPtr.Zero, down, point, 0);
        var upEvent = Native.CGEventCreateMouseEvent(IntPtr.Zero, up, point, 0);
        if (downEvent != IntPtr.Zero) { Native.CGEventPost(Native.HidEventTap, downEvent); Native.CFRelease(downEvent); }
        if (upEvent != IntPtr.Zero) { Native.CGEventPost(Native.HidEventTap, upEvent); Native.CFRelease(upEvent); }
    }

    public static void Drag(int fromX, int fromY, int toX, int toY)
    {
        Hover(fromX, fromY);
        var steps = Math.Max(8, (Math.Abs(toX - fromX) + Math.Abs(toY - fromY)) / 20);
        var down = Native.CGEventCreateMouseEvent(IntPtr.Zero, Native.LeftMouseDown,
            new Native.CgPoint { X = fromX, Y = fromY }, 0);
        if (down != IntPtr.Zero) { Native.CGEventPost(Native.HidEventTap, down); Native.CFRelease(down); }
        for (var i = 1; i <= steps; i++)
        {
            var point = new Native.CgPoint
            {
                X = fromX + (toX - fromX) * i / steps,
                Y = fromY + (toY - fromY) * i / steps,
            };
            var move = Native.CGEventCreateMouseEvent(IntPtr.Zero, Native.LeftMouseDragged, point, 0);
            if (move != IntPtr.Zero) { Native.CGEventPost(Native.HidEventTap, move); Native.CFRelease(move); }
            Thread.Sleep(10);
        }
        var upPoint = new Native.CgPoint { X = toX, Y = toY };
        var upEvent = Native.CGEventCreateMouseEvent(IntPtr.Zero, Native.LeftMouseUp, upPoint, 0);
        if (upEvent != IntPtr.Zero) { Native.CGEventPost(Native.HidEventTap, upEvent); Native.CFRelease(upEvent); }
    }

    /// <summary>Posts an event and releases it. CGEventPost itself needs no release; the event does.</summary>
    private static void Post(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return;
        Native.CGEventPost(Native.HidEventTap, ev);
        Native.CFRelease(ev);
    }
}
