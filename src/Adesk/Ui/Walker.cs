namespace Adesk.Ui;

/// <summary>Budget for one walk. Hitting a budget marks the snapshot incomplete instead of lying.</summary>
public sealed class WalkBudget
{
    public int MaxDepth { get; init; } = 30;
    public int MaxSiblings { get; init; } = 2000;
    public int MaxNodes { get; init; } = 5000;
    public int DeadlineMs { get; init; } = 5000;
}

public sealed class WalkResult
{
    public required UiNodeData Root { get; init; }
    public bool Complete { get; init; }
    public int NodeCount { get; init; }
    public required Dictionary<int, RefEntry> Refs { get; init; }
}

/// <summary>
/// Shared walk engine: budget enforcement, skeleton cuts, interactive-only pruning and
/// unique-identity ref allocation run here, so the UIA adapter and any test fake ship the same logic.
/// </summary>
public static class Walker
{
    public sealed class Options
    {
        public bool InteractiveOnly { get; init; }
        public bool Skeleton { get; init; }
        public int? MaxDepth { get; init; }
        public WalkBudget Budget { get; init; } = new();
    }

    public interface ISource
    {
        UiNodeData Root();
        IReadOnlyList<UiNodeData> Children(UiNodeData node, WalkBudget budget);
    }

    public static WalkResult Walk(ISource source, Options options)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(options.Budget.DeadlineMs);
        var nodeCount = 0;
        var complete = true;
        var identityCounts = new Dictionary<string, int>();
        var maxDepth = options.MaxDepth ?? options.Budget.MaxDepth;

        UiNodeData? Build(UiNodeData node, int depth, List<int> path)
        {
            if (nodeCount >= options.Budget.MaxNodes || DateTime.UtcNow > deadline)
            {
                complete = false;
                return null;
            }
            nodeCount++;
            node.ScopePath = path;
            identityCounts[node.IdentityKey] = identityCounts.GetValueOrDefault(node.IdentityKey) + 1;

            // skeleton mode: summarize container subtrees from level 2 down; leaves stay visible
            if (options.Skeleton && depth >= 1 && IsContainer(node))
            {
                node.SkeletonCut = true;
                return node;
            }

            var children = source.Children(node, options.Budget).ToList();
            if (children.Count > options.Budget.MaxSiblings)
            {
                children = children[..options.Budget.MaxSiblings];
                complete = false;
            }
            foreach (var child in children)
            {
                if (depth + 1 > maxDepth)
                {
                    complete = false;
                    break;
                }
                var childPath = new List<int>(path) { node.Children.Count };
                node.Children.Add(child);
                if (Build(child, depth + 1, childPath) is null) break;
            }
            return node;
        }

        var root = Build(source.Root(), 0, []) ?? throw new InvalidOperationException("walker produced no root");

        if (options.Skeleton) FillChildCounts(root, source);
        if (options.InteractiveOnly) PruneToInteractive(root);

        var refs = new Dictionary<int, RefEntry>();
        var index = 0;
        AssignRefs(root);
        return new WalkResult { Root = root, Complete = complete, NodeCount = nodeCount, Refs = refs };

        void AssignRefs(UiNodeData n)
        {
            // depth-first so parents get lower numbers than their children
            if (n.IsInteractive && identityCounts.GetValueOrDefault(n.IdentityKey) == 1)
            {
                index++;
                n.Ref = Ids.FormatRef("__sid__", index);
                refs[index] = new RefEntry
                {
                    Pid = n.Pid,
                    Hwnd = n.Hwnd,
                    RuntimeId = n.RuntimeId,
                    Role = n.Role,
                    Name = n.Name,
                    AutomationId = n.AutomationId,
                    ClassName = n.ClassName,
                    ScopePath = n.ScopePath,
                    Patterns = n.Patterns,
                    CapturedUnique = true,
                    Rect = [n.Rect.X, n.Rect.Y, n.Rect.W, n.Rect.H],
                };
            }
            foreach (var child in n.Children) AssignRefs(child);
        }
    }

    private static bool IsContainer(UiNodeData node)
    {
        var role = node.Role.ToLowerInvariant();
        return role is "pane" or "group" or "document" or "custom" or "tab" or "tree"
            or "table" or "list" or "data grid" or "client" or "content pane";
    }

    private static void FillChildCounts(UiNodeData node, ISource source)
    {
        if (node.SkeletonCut)
        {
            node.ChildCount = source.Children(node, new WalkBudget()).Count;
            return;
        }
        foreach (var child in node.Children) FillChildCounts(child, source);
    }

    /// <summary>Drops non-interactive leaves; keeps any container that still holds interactive nodes.</summary>
    private static bool PruneToInteractive(UiNodeData node)
    {
        node.Children.RemoveAll(child => !PruneToInteractive(child));
        return node.IsInteractive || node.Children.Count > 0 || node.SkeletonCut;
    }
}
