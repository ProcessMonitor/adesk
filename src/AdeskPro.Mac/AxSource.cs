using AdeskPro.Ui;

namespace AdeskPro.Mac;

/// <summary>The macOS Walker.ISource: projects AX elements into the platform-neutral node model.
/// Children are read on demand and their handles flow into <see cref="UiNodeData.Raw"/> so that
/// actions can use the live element without a second lookup.</summary>
internal sealed class AxSource : Walker.ISource
{
    private readonly AxElement _root;

    internal AxSource(AxElement root) { _root = root; }

    public UiNodeData Root() => Project(_root);

    public IReadOnlyList<UiNodeData> Children(UiNodeData node, WalkBudget budget)
    {
        if (node.Raw is not AxElement raw) return [];
        var children = raw.GetChildren();
        if (children.Count == 0) return [];
        var list = new List<UiNodeData>(children.Count);
        foreach (var child in children)
        {
            // Each child is projected; the AxElement stays alive through Raw until the command ends.
            list.Add(Project(child));
        }
        return list;
    }

    /// <summary>Projects one AX element. Roles are mapped to the same vocabulary the Windows
    /// adapter produces where possible, so an agent's role filters work on both platforms.</summary>
    internal static UiNodeData Project(AxElement element)
    {
        var axRole = element.GetString(AxNames.Role) ?? "";
        var subrole = element.GetString(AxNames.Subrole) ?? "";
        var title = element.GetString(AxNames.Title) ?? "";
        var description = element.GetString(AxNames.Description) ?? "";
        var identifier = element.GetString(AxNames.Identifier) ?? "";
        var roleDescription = element.GetString(AxNames.RoleDescription) ?? "";
        var enabled = element.GetBool(AxNames.Enabled) ?? true;
        var valueText = element.GetString(AxNames.Value) ?? "";
        var actions = element.GetActionNames();

        var node = new UiNodeData
        {
            Role = string.IsNullOrEmpty(roleDescription) ? MapRole(axRole) : roleDescription,
            RoleId = axRole,
            Name = DisplayName(axRole, title, description, valueText),
            AutomationId = identifier,
            ClassName = subrole,
            Pid = element.Pid,
            IsKeyboardFocusable = IsFocusableRole(axRole),
            IsOffscreen = !enabled,
            Rect = element.Rect,
            Patterns = MapPatterns(axRole, actions, valueText),
            Raw = element,
        };

        return node;
    }

    /// <summary>
    /// The single definition of "what this element is called", shared by snapshot projection and
    /// live ref re-identification. AX exposes widget text in AXValue rather than AXTitle, so text
    /// fields must fall back to it — otherwise a saved ref can never be matched again.
    /// </summary>
    internal static string DisplayName(string axRole, string title, string description, string valueText)
    {
        if (!string.IsNullOrEmpty(title)) return title;
        if (!string.IsNullOrEmpty(description)) return description;
        return axRole is "AXTextField" or "AXTextArea" or "AXComboBox" ? valueText : "";
    }

    /// <summary>AXRoles are already stable identifiers; this only normalises casing/punctuation so
    /// role filters like "button" or "Window" behave the same on both platforms.</summary>
    private static string MapRole(string axRole) => axRole switch
    {
        "" => "custom",
        "AXWindow" => "window",
        "AXButton" => "button",
        "AXCheckBox" => "check box",
        "AXRadioButton" => "radio button",
        "AXTextField" => "edit",
        "AXTextArea" => "edit",
        "AXStaticText" => "text",
        "AXMenuItem" => "menu item",
        "AXMenuBar" => "menu bar",
        "AXList" => "list",
        "AXTable" => "table",
        "AXRow" => "row",
        "AXColumn" => "column",
        "AXOutline" => "tree",
        "AXGroup" => "group",
        "AXScrollArea" => "pane",
        "AXTabGroup" => "tab",
        "AXRadioGroup" => "group",
        "AXComboBox" => "combo box",
        "AXPopUpButton" => "combo box",
        "AXSlider" => "slider",
        "AXImage" => "image",
        "AXLink" => "hyperlink",
        "AXCell" => "cell",
        "AXApplication" => "application",
        "AXSheet" => "pane",
        "AXDialog" => "window",
        "AXSplitter" => "separator",
        "AXToolbar" => "tool bar",
        "AXProgressIndicator" => "progress bar",
        _ => axRole.StartsWith("AX", StringComparison.Ordinal) ? axRole[2..].ToLowerInvariant() : axRole,
    };

    /// <summary>Pattern vocabulary shared with the Windows adapter: invoke / value / toggle /
    /// expandcollapse / selectionitem / scroll.</summary>
    private static List<string> MapPatterns(string axRole, IReadOnlyList<string> actions, string valueText)
    {
        var patterns = new List<string>();
        if (actions.Contains("AXPress")) patterns.Add("invoke");
        if (axRole is "AXCheckBox" or "AXRadioButton" || actions.Contains("AXToggle")) patterns.Add("toggle");
        if (axRole is "AXTextField" or "AXTextArea" or "AXComboBox" or "AXSlider" or "AXProgressIndicator")
            patterns.Add("value");
        if (actions.Contains("AXExpand") || actions.Contains("AXCollapse")) patterns.Add("expandcollapse");
        if (axRole is "AXRow" or "AXCell" or "AXMenuItem" || actions.Contains("AXPick")) patterns.Add("selectionitem");
        if (axRole is "AXScrollArea" or "AXOutline" or "AXTable" or "AXList" || actions.Contains("AXScrollDownByPage"))
            patterns.Add("scroll");
        return patterns;
    }

    private static bool IsFocusableRole(string axRole) => axRole is
        "AXTextField" or "AXTextArea" or "AXButton" or "AXCheckBox" or "AXRadioButton" or
        "AXComboBox" or "AXPopUpButton" or "AXMenuItem" or "AXList" or "AXOutline" or
        "AXTable" or "AXSlider" or "AXRow" or "AXLink";
}
