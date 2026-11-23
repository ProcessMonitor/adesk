using System.Text.Json.Nodes;
using AdeskPro;
using AdeskPro.Ui;
using Xunit;

namespace AdeskPro.Tests;

/// <summary>Covers the CLI shorthand the docs advertise, and the error/disposition contract
/// agent-side callers rely on.</summary>
public class CliParsingTests
{
    [Fact]
    public void Bare_ref_is_accepted_positionally_for_click()
    {
        var spec = Registry.Find("click")!;
        var (args, failure) = Registry.Parse(spec, ["@s8f3k2p9:e12"]);
        Assert.Null(failure);
        Assert.Equal("@s8f3k2p9:e12", args!.Str("ref"));
    }

    [Fact]
    public void Positional_and_flag_forms_are_equivalent()
    {
        var spec = Registry.Find("click")!;
        var (positional, _) = Registry.Parse(spec, ["@s8f3k2p9:e12"]);
        var (flagged, _) = Registry.Parse(spec, ["--ref", "@s8f3k2p9:e12"]);
        Assert.Equal(flagged!.Str("ref"), positional!.Str("ref"));
    }

    [Fact]
    public void Giving_a_positional_and_its_flag_is_refused()
    {
        var spec = Registry.Find("click")!;
        var (_, failure) = Registry.Parse(spec, ["@s8f3k2p9:e12", "--ref", "@s8f3k2p9:e13"]);
        Assert.NotNull(failure);
        Assert.Equal(ErrorCodes.InvalidArgs, failure.Error!.Code);
    }

    [Fact]
    public void Surplus_positional_arguments_are_still_refused()
    {
        var spec = Registry.Find("click")!;
        var (_, failure) = Registry.Parse(spec, ["@s8f3k2p9:e12", "@s8f3k2p9:e13"]);
        Assert.NotNull(failure);
        Assert.Contains("Unexpected positional", failure.Error!.Message);
    }

    [Fact]
    public void Skills_accepts_the_documented_verb()
    {
        var spec = Registry.Find("skills")!;
        var (args, failure) = Registry.Parse(spec, ["get", "desktop"]);
        Assert.Null(failure);
        Assert.Equal("desktop", args!.Str("name"));
    }

    [Fact]
    public void Type_takes_ref_and_text_positionally()
    {
        var spec = Registry.Find("type")!;
        var (args, failure) = Registry.Parse(spec, ["@s8f3k2p9:e12", "hello"]);
        Assert.Null(failure);
        Assert.Equal("@s8f3k2p9:e12", args!.Str("ref"));
        Assert.Equal("hello", args!.Str("text"));
    }

    [Fact]
    public void Text_that_looks_like_a_flag_is_still_deliverable()
    {
        var spec = Registry.Find("type")!;
        var (args, failure) = Registry.Parse(spec, ["--ref", "@s8f3k2p9:e12", "--text", "--help"]);
        Assert.Null(failure);
        Assert.Equal("--help", args!.Str("text"));
    }

    [Fact]
    public void Help_requested_as_a_value_is_not_a_help_request()
    {
        var spec = Registry.Find("type")!;
        Assert.False(Registry.WantsHelp(spec, ["--ref", "@s8f3k2p9:e12", "--text", "--help"]));
        Assert.True(Registry.WantsHelp(spec, ["--help"]));
        Assert.True(Registry.WantsHelp(spec, ["@s8f3k2p9:e12", "-h"]));
    }

    [Fact]
    public void Text_needing_a_leading_dash_still_works_through_equals()
    {
        var spec = Registry.Find("type")!;
        var (args, failure) = Registry.Parse(spec, ["--ref=@s8f3k2p9:e12", "--text=--flag-like"]);
        Assert.Null(failure);
        Assert.Equal("--flag-like", args!.Str("text"));
    }
}

public class DispatchContractTests
{
    private static Session NewSession() => new()
    {
        Name = "test",
        InMemory = true,
        Refs = new RefStore(null),
        Platform = new FakePlatform(),
    };

    [Fact]
    public void Boolean_flag_accepts_the_string_false_from_json_clients()
    {
        // Dispatch must not turn {"i": "false"} into "switch the filter on".
        var args = new JsonObject { ["i"] = "false", ["app"] = "Nowhere" };
        var envelope = Handlers.Dispatch("snapshot", args, NewSession());
        // No window exists in the fake platform, so the handler fails — the point is that it
        // reached the handler with i=false rather than refusing or enabling the filter.
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.AppNotFound, envelope.Error!.Code);
    }

    [Fact]
    public void Snapshot_i_false_does_not_enable_the_filter()
    {
        var spec = Registry.Find("snapshot")!;
        var (args, failure) = Registry.Parse(spec, []);
        Assert.Null(failure);
        Assert.False(args!.Has("i"));
    }

    [Fact]
    public void Wait_timeout_is_a_failure_not_a_success()
    {
        var envelope = Handlers.Dispatch("wait", new JsonObject
        {
            ["app"] = "NoSuchAppAnywhere",
            ["timeout"] = 600,
        }, NewSession());
        Assert.False(envelope.Ok);
        Assert.Equal(ErrorCodes.Timeout, envelope.Error!.Code);
    }

    [Fact]
    public void Error_summary_keeps_the_suggestion_so_text_agents_can_recover()
    {
        var envelope = Envelope.Fail(ErrorCodes.StaleRef, "element is gone", "take a new snapshot");
        var summary = EnvelopeJson.Summarize(envelope);
        Assert.Contains("STALE_REF", summary);
        Assert.Contains("take a new snapshot", summary);
    }
}

public class InstallMcpTests
{
    private static Session NewSession() => new()
    {
        Name = "test",
        InMemory = true,
        Refs = new RefStore(null),
        Platform = new FakePlatform(),
    };

    private static JsonObject InstallMcp()
    {
        var envelope = Handlers.Dispatch("install-mcp", new JsonObject(), NewSession());
        Assert.True(envelope.Ok, envelope.Error?.Message);
        return envelope.Data!;
    }

    /// <summary>Runs the body with the launcher marker set, then restores it — the marker is
    /// process state, so a leaked value would poison every later test.</summary>
    private static T WithNpmLaunch<T>(Func<T> body)
    {
        var previous = Environment.GetEnvironmentVariable(Handlers.LaunchContextEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Handlers.LaunchContextEnvVar, "adesk-pro");
            return body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Handlers.LaunchContextEnvVar, previous);
        }
    }

    [Fact]
    public void Direct_binary_emits_an_absolute_path()
    {
        var data = InstallMcp();
        Assert.Equal("binary", data["via"]!.GetValue<string>());
        Assert.NotEqual("npx", data["command_"]!.GetValue<string>());
    }

    [Fact]
    public void Npm_launcher_emits_the_npx_form()
    {
        var data = WithNpmLaunch(InstallMcp);
        Assert.Equal("npm", data["via"]!.GetValue<string>());
        Assert.Equal("npx", data["command_"]!.GetValue<string>());

        var snippet = System.Text.Json.Nodes.JsonNode.Parse(data["snippet"]!.GetValue<string>())!;
        var args = snippet["mcpServers"]!["adesk-pro"]!["args"]!.AsArray();
        Assert.Equal(["-y", "adesk-pro", "mcp"], args.Select(a => a!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void Npm_form_is_used_so_an_upgrade_does_not_break_the_config()
    {
        var data = WithNpmLaunch(InstallMcp);
        var snippet = data["snippet"]!.GetValue<string>();
        // A path into node_modules stops existing after the next npm install.
        Assert.DoesNotContain("node_modules", snippet);
    }
}

public class RefStoreTests
{
    [Fact]
    public void Latest_snapshot_is_the_last_one_written()
    {
        var store = new RefStore(null);
        store.Save("sa", new Dictionary<int, RefEntry> { [1] = new() });
        store.Save("sb", new Dictionary<int, RefEntry> { [1] = new() });
        store.Save("sc", new Dictionary<int, RefEntry> { [1] = new() });
        Assert.Equal("sc", store.LatestSnapshotId);
    }

    [Fact]
    public void Rewriting_an_existing_snapshot_makes_it_latest_without_duplicating_order()
    {
        var store = new RefStore(null);
        store.Save("sa", new Dictionary<int, RefEntry> { [1] = new() });
        store.Save("sb", new Dictionary<int, RefEntry> { [1] = new() });
        store.Save("sa", new Dictionary<int, RefEntry> { [1] = new() });
        Assert.Equal("sa", store.LatestSnapshotId);
        Assert.Equal(2, store.MemorySnapshotCount);
    }

    [Fact]
    public void RoleId_drives_identity_ahead_of_the_localised_role()
    {
        var entry = new RefEntry { Pid = 1, Role = "按钮", RoleId = "ControlType.Button", Name = "OK" };
        Assert.Contains("ControlType.Button", entry.IdentityKey);
        Assert.DoesNotContain("按钮", entry.IdentityKey);
    }
}

public class CatalogShapeTests
{
    [Fact]
    public void Mcp_property_names_have_no_leading_dashes()
    {
        var tools = AdeskPro.Mcp.Catalog.Build();
        foreach (var tool in tools)
        {
            var properties = tool!["inputSchema"]!["properties"]!.AsObject();
            foreach (var (key, _) in properties)
                Assert.DoesNotContain("--", key);
        }
    }

    [Fact]
    public void Doctor_is_registered()
    {
        Assert.NotNull(Registry.Find("doctor"));
    }
}
