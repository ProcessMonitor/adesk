using System.Windows.Automation;

namespace AdeskPro.Ui;

/// <summary>Everything the walker needs to know about one element, source-agnostic.</summary>
public sealed class UiNodeData
{
    public required string Role { get; set; }
    public string Name { get; set; } = "";
    public string AutomationId { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int[] RuntimeId { get; set; } = [];
    public int Pid { get; set; }
    public long Hwnd { get; set; }
    public bool IsOffscreen { get; set; }
    public bool IsKeyboardFocusable { get; set; }
    public (int X, int Y, int W, int H) Rect { get; set; }
    /// <summary>Pattern names the element supports, e.g. invoke, toggle, value, expandcollapse, selectionitem, scroll.</summary>
    public List<string> Patterns { get; set; } = [];
    /// <summary>Sibling index path from the walked root; the identity fallback when runtime ids go stale.</summary>
    public List<int> ScopePath { get; set; } = [];
    public List<UiNodeData> Children { get; set; } = [];
    public int ChildCount { get; set; }      // reported instead of Children when skeleton-cut
    public bool SkeletonCut { get; set; }
    public string? Ref { get; set; }
    /// <summary>Live UIA element behind this node; null in test fakes.</summary>
    public AutomationElement? Raw { get; set; }

    public bool IsInteractive => Patterns.Count > 0 || IsKeyboardFocusable;

    /// <summary>Identity key used for uniqueness counting and live re-resolution.</summary>
    public string IdentityKey => $"{Pid}|{Role}|{AutomationId}|{Name}|{ClassName}";
}
