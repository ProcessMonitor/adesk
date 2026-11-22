using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;

namespace Adesk.Ui;

public sealed class NoPatternException(string what, string suggestion) : Exception(what)
{
    public string Suggestion { get; } = suggestion;
}

/// <summary>Semantic actions on a live element, falling closed when no pattern exists.</summary>
public static class Actions
{
    public static string Click(AutomationElement el)
    {
        if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return "invoke";
        }
        if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
        {
            ((ExpandCollapsePattern)expand).Expand();
            return "expand";
        }
        if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
        {
            ((SelectionItemPattern)select).Select();
            return "select";
        }
        if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return "toggle";
        }
        throw new NoPatternException("element has no invoke/expand/select/toggle pattern",
            "use 'adesk press' with a shortcut the app exposes, or set focus first");
    }

    public static string Toggle(AutomationElement el)
    {
        if (!el.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            throw new NoPatternException("element has no toggle pattern", "try 'adesk click' instead");
        ((TogglePattern)toggle).Toggle();
        return "toggle";
    }

    public static string Expand(AutomationElement el, bool collapse)
    {
        if (!el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            throw new NoPatternException("element has no expandcollapse pattern", "try 'adesk click'");
        var pattern = (ExpandCollapsePattern)expand;
        if (collapse) pattern.Collapse(); else pattern.Expand();
        return collapse ? "collapse" : "expand";
    }

    public static string Select(AutomationElement el)
    {
        if (!el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            throw new NoPatternException("element has no selectionitem pattern", "try 'adesk click'");
        ((SelectionItemPattern)select).Select();
        return "select";
    }

    public static (string How, string Value) TypeText(AutomationElement el, string text)
    {
        if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
        {
            var vp = (ValuePattern)value;
            if (vp.Current.IsReadOnly)
                throw new NoPatternException("element value is read-only", "nothing to type into");
            vp.SetValue(text);
            return ("value", text);
        }
        el.SetFocus();
        SendUnicode(text);
        return ("keyboard", text);
    }

    public static string SetValue(AutomationElement el, string text)
    {
        if (!el.TryGetCurrentPattern(ValuePattern.Pattern, out var value))
            throw new NoPatternException("element has no value pattern",
                "only editable controls expose the Value pattern; try 'adesk type' for keyboard entry");
        ((ValuePattern)value).SetValue(text);
        return "value";
    }

    public static string ScrollDy(AutomationElement el, int dy)
    {
        AutomationElement? node = el;
        for (var depth = 0; node is not null && depth < 15; depth++, node = ParentOf(node))
        {
            if (node.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll))
            {
                var sp = (ScrollPattern)scroll;
                var current = sp.Current.VerticalScrollPercent;
                var target = Math.Clamp(current + dy, 0, 100);
                sp.SetScrollPercent(-1, target);
                return $"scroll {current:0}% -> {target:0}%";
            }
        }
        throw new NoPatternException("no scrollable ancestor found",
            "the ref may sit in a virtualized list; try scrolling with 'adesk press pgdn' after focusing");
    }

    public static string FocusEl(AutomationElement el)
    {
        el.SetFocus();
        return "focus";
    }

    public static string ClipboardGet() => Clipboard.GetText();

    public static void ClipboardSet(string text) => Clipboard.SetText(text);

    private static AutomationElement? ParentOf(AutomationElement el)
    {
        try { return TreeWalker.ControlViewWalker.GetParent(el); }
        catch (ElementNotAvailableException) { return null; }
    }

    // --- keyboard: SendInput ---

    public static void PressKeys(string combo)
    {
        var keys = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(MapKey).ToArray();
        if (keys.Length == 0) throw new NoPatternException("empty key combo", "e.g. ctrl+s");
        var inputs = new List<INPUT>();
        foreach (var vk in keys[..^1]) inputs.Add(KeyInput(vk, down: true));
        inputs.Add(KeyInput(keys[^1], down: true));
        inputs.Add(KeyInput(keys[^1], down: false));
        for (var i = keys.Length - 2; i >= 0; i--) inputs.Add(KeyInput(keys[i], down: false));
        Send(inputs.ToArray());
    }

    private static void SendUnicode(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var ch in text)
        {
            inputs.Add(UnicodeInput(ch, down: true));
            inputs.Add(UnicodeInput(ch, down: false));
        }
        Send(inputs.ToArray());
    }

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput was blocked");
    }

    private static ushort MapKey(string name)
    {
        var k = name.ToLowerInvariant();
        return k switch
        {
            "ctrl" or "control" => 0x11,
            "alt" => 0x12,
            "shift" => 0x10,
            "win" or "meta" => 0x5B,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            "esc" or "escape" => 0x1B,
            "space" => 0x20,
            "backspace" or "bs" => 0x08,
            "del" or "delete" => 0x2E,
            "ins" or "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pgup" => 0x21,
            "pgdn" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ when k.Length == 1 && char.IsAsciiLetterOrDigit(k[0]) => (ushort)char.ToUpperInvariant(k[0]),
            _ when k.Length is 2 or 3 && k[0] == 'f' && short.TryParse(k[1..], out var f) && f is >= 1 and <= 12 => (ushort)(0x6F + f),
            _ => throw new NoPatternException($"unknown key '{name}'", "use names like ctrl, alt, shift, enter, pgdn, f5, a"),
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
        public static INPUT Keyboard(ushort vk, bool down, bool unicode = false) => new()
        {
            Type = 1,
            U = new InputUnion
            {
                Ki = new KEYBDINPUT
                {
                    Vk = unicode ? (ushort)0 : vk,
                    Scan = unicode ? vk : (ushort)0,
                    Flags = unicode
                        ? 0x0004 | (down ? 0u : 0x0002u)          // KEYEVENTF_UNICODE [| KEYEVENTF_KEYUP]
                        : (down ? 0u : 0x0002u),                  // KEYEVENTF_KEYUP
                    Time = 0,
                    ExtraInfo = IntPtr.Zero,
                },
            },
        };
    }

    private static INPUT KeyInput(ushort vk, bool down) => INPUT.Keyboard(vk, down);
    private static INPUT UnicodeInput(char ch, bool down) => INPUT.Keyboard(ch, down, unicode: true);

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
