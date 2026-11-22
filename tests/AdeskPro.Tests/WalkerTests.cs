using AdeskPro.Ui;
using Xunit;

namespace AdeskPro.Tests;

/// <summary>Fake source: children live outside the nodes, so the walker builds them the way it builds UIA trees.</summary>
internal sealed class FakeSource(UiNodeData root, Dictionary<UiNodeData, List<UiNodeData>> children)
    : Walker.ISource
{
    public UiNodeData Root() => root;

    public IReadOnlyList<UiNodeData> Children(UiNodeData node, WalkBudget budget) =>
        children.TryGetValue(node, out var list) ? list : [];
}

internal static class Fakes
{
    public static UiNodeData Node(string role, string name = "", bool interactive = false, string automationId = "")
    {
        var patterns = new List<string>();
        if (interactive) patterns.Add("invoke");
        return new UiNodeData
        {
            Role = role,
            Name = name,
            AutomationId = automationId,
            Pid = 42,
            IsKeyboardFocusable = interactive,
            Patterns = patterns,
        };
    }
}

public class WalkerTests
{
    private static (UiNodeData Root, FakeSource Source) SampleApp()
    {
        var root = Fakes.Node("window", "Notepad");
        var pane = Fakes.Node("pane", "content");
        var unique = Fakes.Node("button", "Unique", interactive: true, automationId: "btn1");
        var ok1 = Fakes.Node("button", "OK", interactive: true, automationId: "ok");
        var ok2 = Fakes.Node("button", "OK", interactive: true, automationId: "ok");
        var label = Fakes.Node("text", "static label");
        var children = new Dictionary<UiNodeData, List<UiNodeData>>
        {
            [root] = [pane],
            [pane] = [unique, ok1, ok2, label],
        };
        return (root, new FakeSource(root, children));
    }

    [Fact]
    public void Unique_interactive_element_gets_a_ref()
    {
        var (root, source) = SampleApp();
        var result = Walker.Walk(source, new Walker.Options());
        var refed = result.Refs.Values.Where(r => r.Name == "Unique").ToList();
        Assert.Single(refed);
        Assert.True(refed[0].CapturedUnique);
    }

    [Fact]
    public void Identical_twins_get_no_ref()
    {
        var (root, source) = SampleApp();
        var result = Walker.Walk(source, new Walker.Options());
        Assert.Equal(1, result.Refs.Count);   // only the unique button
        Assert.All(result.Refs.Values, r => Assert.NotEqual("OK", r.Name));
    }

    [Fact]
    public void Interactive_only_prunes_static_leaves()
    {
        var (root, source) = SampleApp();
        var result = Walker.Walk(source, new Walker.Options { InteractiveOnly = true });
        Assert.False(HasName(result.Root, "static label"));
    }

    private static bool HasName(UiNodeData n, string name) =>
        n.Name == name || n.Children.Any(c => HasName(c, name));

    [Fact]
    public void Skeleton_cuts_report_children_count()
    {
        var root = Fakes.Node("window", "App");
        var deep = Fakes.Node("pane", "deep");
        var children = new Dictionary<UiNodeData, List<UiNodeData>>
        {
            [root] = [deep],
            [deep] = Enumerable.Range(0, 3).Select(i => Fakes.Node("button", $"b{i}", interactive: true)).ToList(),
        };

        var result = Walker.Walk(new FakeSource(root, children), new Walker.Options { Skeleton = true });

        Assert.True(result.Root.Children[0].SkeletonCut);
        Assert.Equal(3, result.Root.Children[0].ChildCount);
        Assert.Empty(result.Root.Children[0].Children);
    }

    [Fact]
    public void Skeleton_keeps_visible_leaves_referenced()
    {
        var root = Fakes.Node("window", "App");
        var direct = Fakes.Node("button", "direct", interactive: true);
        var pane = Fakes.Node("pane", "p");
        var children = new Dictionary<UiNodeData, List<UiNodeData>>
        {
            [root] = [direct, pane],
            [pane] = [Fakes.Node("button", "hidden", interactive: true)],
        };

        var result = Walker.Walk(new FakeSource(root, children), new Walker.Options { Skeleton = true });

        Assert.True(result.Complete);
        Assert.Equal(1, result.Refs.Count);   // "direct" refed; "hidden" lives under a cut
    }

    [Fact]
    public void Node_budget_marks_snapshot_incomplete()
    {
        var root = Fakes.Node("window", "App");
        var buttons = Enumerable.Range(0, 50).Select(i => Fakes.Node("button", $"b{i}", interactive: true)).ToList();
        var source = new FakeSource(root, new Dictionary<UiNodeData, List<UiNodeData>> { [root] = buttons });

        var result = Walker.Walk(source, new Walker.Options { Budget = new WalkBudget { MaxNodes = 10 } });

        Assert.False(result.Complete);
        Assert.True(result.NodeCount <= 10);
    }

    [Fact]
    public void Max_depth_marks_snapshot_incomplete()
    {
        var root = Fakes.Node("window", "App");
        var pane = Fakes.Node("button-holder", "p");   // not a container role, so skeleton rules don't apply
        var child = Fakes.Node("button", "b", interactive: true);
        var source = new FakeSource(root, new Dictionary<UiNodeData, List<UiNodeData>>
        {
            [root] = [pane],
            [pane] = [child],
        });

        var result = Walker.Walk(source, new Walker.Options { MaxDepth = 1 });

        Assert.False(result.Complete);
    }

    [Fact]
    public void Refs_carry_identity_for_live_resolution()
    {
        var (root, source) = SampleApp();
        var result = Walker.Walk(source, new Walker.Options());
        var entry = Assert.Single(result.Refs.Values);
        Assert.Equal(42, entry.Pid);
        Assert.Equal("btn1", entry.AutomationId);
        Assert.NotEmpty(entry.IdentityKey);
        Assert.Equal(2, entry.ScopePath.Count);   // window -> pane -> button
    }
}

public class IdsTests
{
    [Theory]
    [InlineData("@sabc123:e1", "sabc123", 1)]
    [InlineData("@s8f3k2p9:e42", "s8f3k2p9", 42)]
    public void Valid_refs_parse(string token, string sid, int index)
    {
        var parsed = Ids.ParseRef(token);
        Assert.NotNull(parsed);
        Assert.Equal((sid, index), parsed!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sabc:e1")]
    [InlineData("@sabc")]
    [InlineData("@sabc:x1")]
    [InlineData("@sabc:e0")]
    [InlineData("@xabc:e1")]
    public void Invalid_refs_are_rejected(string token)
    {
        Assert.Null(Ids.ParseRef(token));
    }

    [Fact]
    public void Snapshot_ids_are_unique_and_canonical()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 100; i++)
        {
            var sid = Ids.NewSnapshotId();
            Assert.True(Ids.ValidSnapshotId(sid), sid);
            Assert.True(seen.Add(sid), "snapshot ids must not collide");
        }
    }
}

public class RegistryTests
{
    [Fact]
    public void Every_command_is_complete_and_unique()
    {
        Assert.All(Registry.Commands, spec =>
        {
            Assert.False(string.IsNullOrWhiteSpace(spec.Name));
            Assert.False(string.IsNullOrWhiteSpace(spec.Description));
            Assert.NotNull(spec.Run);
        });
        var names = Registry.Commands.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Missing_required_flag_is_reported()
    {
        var spec = Registry.Find("click")!;
        var (args, failure) = Registry.Parse(spec, []);
        Assert.Null(args);
        Assert.NotNull(failure);
        Assert.Contains("--ref", failure.Error!.Message);
    }

    [Fact]
    public void Unknown_flag_is_reported()
    {
        var spec = Registry.Find("click")!;
        var (_, failure) = Registry.Parse(spec, ["--ref", "@sabc:e1", "--bogus", "x"]);
        Assert.NotNull(failure);
        Assert.Contains("--bogus", failure.Error!.Message);
    }

    [Fact]
    public void Bad_ref_is_reported()
    {
        var spec = Registry.Find("click")!;
        var (_, failure) = Registry.Parse(spec, ["--ref", "not-a-ref"]);
        Assert.NotNull(failure);
        Assert.Equal(ErrorCodes.InvalidArgs, failure.Error!.Code);
    }

    [Fact]
    public void Integer_flag_parses()
    {
        var spec = Registry.Find("snapshot")!;
        var (args, failure) = Registry.Parse(spec, ["--pid", "42"]);
        Assert.Null(failure);
        Assert.Equal(42, args!.Int("pid"));
    }

    [Fact]
    public void Catalog_covers_every_command()
    {
        var tools = AdeskPro.Mcp.Catalog.Build();
        Assert.Equal(Registry.Commands.Length, tools.Count);
    }
}
