namespace AdeskPro.Platform;

/// <summary>Decides how a click is delivered, from the element's pattern list alone. Pure and unit-testable.</summary>
public static class ClickPlanner
{
    /// <summary>invoke → expandcollapse → selectionitem → toggle → element-center mouse click.</summary>
    public static string Decide(IReadOnlyList<string> patterns)
    {
        if (patterns.Contains("invoke")) return "invoke";
        if (patterns.Contains("expandcollapse")) return "expandcollapse";
        if (patterns.Contains("selectionitem")) return "selectionitem";
        if (patterns.Contains("toggle")) return "toggle";
        return "mouse-coords";
    }
}
