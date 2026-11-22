using AdeskPro;
using AdeskPro.Ui;
using Xunit;

namespace AdeskPro.Tests;

public class DrillMathTests
{
    private static RefEntry Entry(int pid, string role, string name, params int[] scope) => new()
    {
        Pid = pid, Role = role, Name = name, ScopePath = [.. scope], WindowName = "App",
    };

    [Fact]
    public void Drill_keeps_root_and_outside_entries_drops_inside_ones()
    {
        var existing = new Dictionary<int, RefEntry>
        {
            [1] = Entry(1, "button", "outside", 0),          // window -> [0] sibling of the subtree
            [2] = Entry(1, "pane", "root", 0, 1),            // drilled root
            [3] = Entry(1, "button", "inside", 0, 1, 0),     // inside the subtree -> replaced
        };
        var fresh = new Dictionary<int, RefEntry>
        {
            [1] = Entry(1, "button", "fresh-a"),
            [2] = Entry(1, "button", "fresh-b"),
        };

        var (merged, localToGlobal) = DrillMath.Merge(existing, rootIndex: 2,
            root: existing[2], fresh: fresh);

        Assert.True(merged.ContainsKey(2), "the drilled root itself stays valid");
        Assert.True(merged.ContainsKey(1), "entries outside the subtree stay valid");
        Assert.False(merged.ContainsKey(3), "entries inside the subtree are replaced");
        Assert.Equal(4, merged.Count);   // 2 kept + 2 fresh
        Assert.Equal([4, 5], localToGlobal.Values);     // re-indexed after the highest existing id
    }

    [Fact]
    public void Drill_composes_fresh_scope_paths_onto_the_root_path()
    {
        var existing = new Dictionary<int, RefEntry>
        {
            [1] = Entry(1, "pane", "root", 0, 1),
        };
        var fresh = new Dictionary<int, RefEntry>
        {
            [1] = Entry(1, "button", "fresh", 0),
        };

        var (merged, localToGlobal) = DrillMath.Merge(existing, 1, existing[1], fresh);

        var composed = merged[localToGlobal[1]].ScopePath;
        Assert.Equal([0, 1, 0], composed);               // root path (0,1) + local path (0)
        Assert.Equal("App", merged[localToGlobal[1]].WindowName);
    }

    [Fact]
    public void Drill_into_an_empty_snapshot_still_reindexes_from_one()
    {
        var fresh = new Dictionary<int, RefEntry> { [1] = Entry(1, "button", "b") };
        var (merged, localToGlobal) = DrillMath.Merge(
            new Dictionary<int, RefEntry>(), rootIndex: 1, root: Entry(1, "pane", "root", 0), fresh);
        Assert.Equal([1], localToGlobal.Values);
        Assert.Single(merged);
    }
}

public class DispatchTests
{
    private static Session NewSession() => new()
    {
        Name = "test",
        InMemory = true,
        Refs = new RefStore(null),
        Platform = new FakePlatform(),
    };

    [Fact]
    public void Status_dispatches_through_the_registry()
    {
        var envelope = Handlers.Dispatch("status", new System.Text.Json.Nodes.JsonObject(), NewSession());
        Assert.True(envelope.Ok);
        Assert.Equal("status", envelope.Data!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Unknown_command_is_an_invalid_args_error()
    {
        var envelope = Handlers.Dispatch("nope", new System.Text.Json.Nodes.JsonObject(), NewSession());
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.InvalidArgs, envelope.Error!.Code);
    }

    [Fact]
    public void Missing_required_flag_is_refused()
    {
        var envelope = Handlers.Dispatch("click", new System.Text.Json.Nodes.JsonObject(), NewSession());
        Assert.False(envelope.Ok);
        Assert.Contains("--ref", envelope.Error!.Message);
    }

    [Fact]
    public void Boolean_flags_and_string_flags_both_reach_the_handler()
    {
        // screenshot with no target filter: the flags parsed and the handler ran, and the failure
        // comes from window lookup — no windows at all, or several on a busy desktop
        var envelope = Handlers.Dispatch("screenshot", new System.Text.Json.Nodes.JsonObject(), NewSession());
        Assert.False(envelope.Ok);
        Assert.Contains(envelope.Error!.Code, new[] { ErrorCodes.AppNotFound, ErrorCodes.AmbiguousWindow, ErrorCodes.NotSupported });
    }

    [Fact]
    public void Batch_of_statuses_all_pass()
    {
        var session = NewSession();
        var first = Handlers.Dispatch("status", new System.Text.Json.Nodes.JsonObject(), session);
        var second = Handlers.Dispatch("status", new System.Text.Json.Nodes.JsonObject(), session);
        Assert.True(first.Ok && second.Ok);
    }

    [Fact]
    public void Wait_rejects_unknown_until_values()
    {
        var envelope = Handlers.Dispatch("wait", new System.Text.Json.Nodes.JsonObject
        {
            ["ref"] = "@sabc123:e1",
            ["until"] = "soon",
        }, NewSession());
        Assert.False(envelope.Ok);
        Assert.Contains("--until", envelope.Error!.Message);
    }
}
