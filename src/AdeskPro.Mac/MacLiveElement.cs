using AdeskPro.Platform;
using AdeskPro.Ui;

namespace AdeskPro.Mac;

/// <summary>One live AX element behind the semantic <see cref="ILiveElement"/> surface.</summary>
internal sealed class MacLiveElement : ILiveElement, IDisposable
{
    private readonly AxElement _element;

    internal MacLiveElement(AxElement element) { _element = element; }

    internal AxElement Element => _element;

    public string Role => _element.GetString(AxNames.Role) ?? "";
    public string Name => _element.GetString(AxNames.Title)
        ?? _element.GetString(AxNames.Description) ?? "";
    public string AutomationId => _element.GetString(AxNames.Identifier) ?? "";
    public bool Enabled => _element.GetBool(AxNames.Enabled) ?? true;
    public bool Offscreen => false;

    public (int X, int Y, int W, int H) Rect => _element.Rect;

    public IReadOnlyList<string> Patterns => AxSource.Project(_element).Patterns;

    public string? CurrentValue() => _element.GetString(AxNames.Value);

    /// <summary>Prefers the AX value attribute (fast, reliable); falls back to synthesised typing
    /// after focusing, which is the only path for custom-drawn controls.</summary>
    public (string How, string Value) TypeText(string text)
    {
        if (_element.IsAttributeSettable(AxNames.Value) && _element.SetCString(AxNames.Value, text))
            return ("value", text);
        _element.Perform(CfStringValue);
        MacInput.TypeUnicode(text);
        return ("keyboard", text);
    }

    public string SetValue(string text)
    {
        if (!_element.IsAttributeSettable(AxNames.Value))
            throw new NoPatternException("element value is not settable",
                "try 'adesk-pro type' for keyboard entry");
        if (!_element.SetCString(AxNames.Value, text))
            throw new NoPatternException("the app refused the value change", "try 'adesk-pro type'");
        return "value";
    }

    /// <summary>AXPress → AXShowMenu → AXPick → element-centre mouse click, mirroring the
    /// Windows click ladder.</summary>
    public string ClickDefault()
    {
        if (_element.Perform(AxNames.Press)) return "invoke";
        if (AxSource.Project(_element).Patterns.Contains("expandcollapse"))
        {
            var expand = CfString.Get("AXExpand");
            if (_element.Perform(expand)) return "expandcollapse";
        }
        if (AxSource.Project(_element).Patterns.Contains("selectionitem"))
        {
            if (_element.Perform(AxNames.Pick)) return "selectionitem";
            if (_element.Perform(CfStringSelect)) return "selectionitem";
        }
        var (x, y, w, h) = Rect;
        if (w <= 0 || h <= 0)
            throw new NoPatternException("element exposes no clickable action and has no on-screen rect",
                "scroll it into view, then click again");
        MacInput.Click(x + w / 2, y + h / 2, right: false, doubleClick: false);
        return "mouse-coords";
    }

    public string Toggle()
    {
        if (_element.Perform(AxNames.Press)) return "press";
        if (_element.Perform(CfStringValue)) return "press";
        throw new NoPatternException("element exposes no toggle action", "try 'adesk-pro click'");
    }

    public string ExpandCollapse(bool collapse)
    {
        var action = CfString.Get(collapse ? "AXCollapse" : "AXExpand");
        if (_element.Perform(action)) return collapse ? "collapse" : "expand";
        throw new NoPatternException($"element exposes no {(collapse ? "collapse" : "expand")} action",
            "try 'adesk-pro click'");
    }

    public string SelectItem()
    {
        if (_element.Perform(AxNames.Pick)) return "select";
        if (_element.Perform(CfStringSelect)) return "select";
        throw new NoPatternException("element exposes no selection action", "try 'adesk-pro click'");
    }

    public string ScrollDy(int dy)
    {
        var direction = dy >= 0 ? CfString.Get("AXScrollDownByPage") : CfString.Get("AXScrollUpByPage");
        var pages = Math.Clamp(Math.Abs(dy) / 30, 1, 5);
        for (var i = 0; i < pages; i++)
        {
            if (!_element.Perform(direction))
                throw new NoPatternException("element exposes no scroll action",
                    "scroll the enclosing scroll area instead, or use 'adesk-pro press pagedown'");
        }
        return $"scroll {(dy >= 0 ? "down" : "up")} {pages} page(s)";
    }

    public void FocusNow() => _element.Perform(CfStringValue);

    public void Dispose() => _element.Dispose();

    private static readonly CfString CfStringValue = CfString.Get("AXFocus");
    private static readonly CfString CfStringSelect = CfString.Get("AXSelect");
}
