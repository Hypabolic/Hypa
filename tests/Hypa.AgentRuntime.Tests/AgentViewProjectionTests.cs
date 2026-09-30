using System.Text.Json;
using Hypa.AgentRuntime.Application;
using Hypa.AgentRuntime.Domain;
using Hypa.AgentRuntime.Domain.AttachConfig;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class AgentViewProjectionTests
{
    [Fact]
    public void Valid_name_grammar_matches_herdr()
    {
        Assert.True(AgentNameGrammar.IsValid("a"));
        Assert.True(AgentNameGrammar.IsValid("reviewer"));
        Assert.True(AgentNameGrammar.IsValid("a1-b_2"));
        Assert.True(AgentNameGrammar.IsValid(new string('a', 32)));
        Assert.False(AgentNameGrammar.IsValid(""));
        Assert.False(AgentNameGrammar.IsValid("Reviewer"));
        Assert.False(AgentNameGrammar.IsValid("1abc"));
        Assert.False(AgentNameGrammar.IsValid("a/b"));
        Assert.False(AgentNameGrammar.IsValid(new string('a', 33)));
    }

    [Fact]
    public void Empty_any_filter_does_not_validate()
    {
        using var doc = JsonDocument.Parse("""{"op":"any","filters":[]}""");
        Assert.False(AgentViewProjection.TryValidate(
            "example.views",
            "working",
            doc.RootElement,
            default,
            out _,
            out var error));
        Assert.Contains("must not be empty", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_filter_keeps_matching_rows()
    {
        using var filter = JsonDocument.Parse("""{"op":"eq","field":"status","value":"working"}""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            "working",
            filter.RootElement,
            default,
            out var spec,
            out var error),
            error);
        var rows = new[]
        {
            Row("p1", "working"),
            Row("p2", "idle"),
        };
        var order = AgentViewProjection.Apply(rows, spec, new AgentViewEvalContext("w1", "t1"));
        Assert.Equal(["p1"], order);
    }

    [Fact]
    public void Invalid_source_fails_closed()
    {
        Assert.False(AgentViewProjection.TryValidate(
            "bad source!",
            null,
            default,
            default,
            out _,
            out var error));
        Assert.Contains("source", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Plugin_source_validates_grammar_without_lookup()
    {
        Assert.True(AgentViewProjection.TryValidate(
            "plugin:example.agent-views",
            null,
            default,
            default,
            out var spec,
            out var error),
            error);
        Assert.Equal("plugin:example.agent-views", spec.Source);

        Assert.False(AgentViewProjection.TryValidate(
            "plugin:",
            null,
            default,
            default,
            out _,
            out var pluginError));
        Assert.Contains("invalid plugin id", pluginError, StringComparison.Ordinal);
    }

    [Fact]
    public void State_change_seq_filters_and_sorts_across_two_transitions()
    {
        using var exists = JsonDocument.Parse("""{"op":"exists","field":"state_change_seq"}""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            null,
            exists.RootElement,
            default,
            out var existsSpec,
            out var existsError),
            existsError);
        var before = new[]
        {
            Row("p1", "idle", seq: null),
            Row("p2", "idle", seq: null),
        };
        Assert.Empty(AgentViewProjection.Apply(before, existsSpec, new AgentViewEvalContext("w1", "t1")));

        var afterFirst = new[]
        {
            Row("p1", "working", seq: 1),
            Row("p2", "idle", seq: null),
        };
        Assert.Equal(["p1"], AgentViewProjection.Apply(
            afterFirst, existsSpec, new AgentViewEvalContext("w1", "t1")));

        var afterSecond = new[]
        {
            Row("p1", "working", seq: 1),
            Row("p2", "blocked", seq: 2),
        };
        Assert.Equal(["p1", "p2"], AgentViewProjection.Apply(
            afterSecond, existsSpec, new AgentViewEvalContext("w1", "t1")));

        using var sort = JsonDocument.Parse("""[{"field":"state_change_seq","order":"desc"}]""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            null,
            exists.RootElement,
            sort.RootElement,
            out var sortSpec,
            out var sortError),
            sortError);
        Assert.Equal(["p2", "p1"], AgentViewProjection.Apply(
            afterSecond, sortSpec, new AgentViewEvalContext("w1", "t1")));
    }

    [Fact]
    public void Filter_only_view_keeps_priority_order()
    {
        using var filter = JsonDocument.Parse("""{"op":"exists","field":"agent"}""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            "mixed",
            filter.RootElement,
            default,
            out var spec,
            out var error),
            error);
        Assert.False(spec.HasSort);
        var rows = new[]
        {
            Row("p-idle", "idle", seq: null, seen: true, attention: 1),
            Row("p-blocked", "blocked", seq: null, seen: true, attention: 4),
            Row("p-working", "working", seq: null, seen: true, attention: 2),
        };
        var spaces = AgentViewProjection.Apply(
            rows, spec, new AgentViewEvalContext("w1", "t1"), AgentPanelSort.Spaces);
        Assert.Equal(["p-idle", "p-blocked", "p-working"], spaces);
        var priority = AgentViewProjection.Apply(
            rows, spec, new AgentViewEvalContext("w1", "t1"), AgentPanelSort.Priority);
        Assert.Equal(["p-blocked", "p-working", "p-idle"], priority);
    }

    [Fact]
    public void Token_sort_places_missing_values_last_in_both_directions()
    {
        var rows = new[]
        {
            TokenRow("p-missing", new Dictionary<string, string>(StringComparer.Ordinal)),
            TokenRow("p-alpha", tokens: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model"] = "alpha",
            }),
            TokenRow("p-zeta", tokens: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model"] = "zeta",
            }),
        };
        using var asc = JsonDocument.Parse("""[{"field":{"token":"model"},"order":"asc"}]""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            null,
            default,
            asc.RootElement,
            out var ascSpec,
            out var ascError),
            ascError);
        Assert.Equal(["p-alpha", "p-zeta", "p-missing"], AgentViewProjection.Apply(
            rows, ascSpec, new AgentViewEvalContext("w1", "t1")));

        using var desc = JsonDocument.Parse("""[{"field":{"token":"model"},"order":"desc"}]""");
        Assert.True(AgentViewProjection.TryValidate(
            "example.views",
            null,
            default,
            desc.RootElement,
            out var descSpec,
            out var descError),
            descError);
        Assert.Equal(["p-zeta", "p-alpha", "p-missing"], AgentViewProjection.Apply(
            rows, descSpec, new AgentViewEvalContext("w1", "t1")));
    }

    private static AgentViewRow Row(string id, string status) =>
        Row(id, status, seq: null, seen: true, attention: 0);

    private static AgentViewRow Row(
        string id,
        string status,
        ulong? seq,
        bool seen = true,
        int attention = 0) =>
        new()
        {
            PaneId = id,
            TabId = "t1",
            WorkspaceId = "w1",
            Agent = "muse",
            Status = status,
            Seen = seen,
            Attention = attention,
            StateChangeSeq = seq,
        };

    private static AgentViewRow TokenRow(string id, IReadOnlyDictionary<string, string> tokens) =>
        new()
        {
            PaneId = id,
            TabId = "t1",
            WorkspaceId = "w1",
            Agent = "muse",
            Status = "idle",
            Seen = true,
            Tokens = tokens,
        };
}
