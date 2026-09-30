using Hypa.AgentRuntime.Application.Metadata;
using Hypa.AgentRuntime.Application.Plugins;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public sealed class PluginResourceStoreTests
{
    [Fact]
    public void Publish_get_list_remove_round_trip()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var published = store.Publish(ReadyRequest(id, revision: 1));
        Assert.True(published.IsOk, published.IsOk ? "" : published.Error.Message);
        Assert.False(published.Value.Ignored);
        Assert.Equal(PluginResourceLimits.FreshnessReady, published.Value.Resource.Freshness);

        var got = store.Get(id.Value);
        Assert.True(got.IsOk);
        Assert.Equal("3 need attention", got.Value.Value.Summary);
        Assert.Equal("work-42", Assert.Single(got.Value.Value.Items).Id);

        var listed = store.List("example.smoke");
        Assert.Equal(id.Value, Assert.Single(listed).Id.Value);

        var removed = store.Remove(id.Value, callerPluginId: "example.smoke");
        Assert.True(removed.IsOk);
        Assert.False(store.Get(id.Value).IsOk);
        Assert.Empty(store.List(null));
    }

    [Fact]
    public void Unknown_projection_fails_closed()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var result = store.Publish(ReadyRequest(id, revision: 1) with
        {
            Schema = "hypa.projection.collection.v0",
        });
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.UnknownProjection, result.Error.Code);
        Assert.Empty(store.List(null));
    }

    [Fact]
    public void Stale_revision_is_ignored_and_keeps_last_good()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        Assert.True(store.Publish(ReadyRequest(id, revision: 2)).IsOk);
        var stale = store.Publish(ReadyRequest(id, revision: 2, summary: "ignored"));
        Assert.True(stale.IsOk);
        Assert.True(stale.Value.Ignored);
        Assert.Equal("3 need attention", store.Get(id.Value).Value.Value.Summary);

        var older = store.Publish(ReadyRequest(id, revision: 1, summary: "older"));
        Assert.True(older.IsOk);
        Assert.True(older.Value.Ignored);
        Assert.Equal(2, store.Get(id.Value).Value.Revision);
    }

    [Fact]
    public void Malformed_item_is_rejected_whole()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        Assert.True(store.Publish(ReadyRequest(id, revision: 1)).IsOk);
        var bad = store.Publish(ReadyRequest(id, revision: 2) with
        {
            Value = new PluginCollectionValue
            {
                Items =
                [
                    new PluginCollectionItem { Id = "work-42", Status = "nope", Attention = 0 },
                ],
            },
        });
        Assert.False(bad.IsOk);
        Assert.Equal(PluginError.ResourceMalformed, bad.Error.Code);
        Assert.Equal(1, store.Get(id.Value).Value.Revision);
    }

    [Fact]
    public void Caps_reject_extra_items_and_resources()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var items = Enumerable.Range(0, PluginResourceLimits.MaxItemsPerCollection + 1)
            .Select(i => new PluginCollectionItem
            {
                Id = "item-" + i,
                Status = "idle",
                Attention = 0,
            })
            .ToArray();
        var overflow = store.Publish(new PluginResourcePublishRequest
        {
            Id = id,
            Schema = PluginResourceLimits.CollectionSchema,
            Revision = 1,
            Value = new PluginCollectionValue { Items = items },
        });
        Assert.False(overflow.IsOk);
        Assert.Equal(PluginError.ResourceFull, overflow.Error.Code);

        for (var i = 0; i < PluginResourceLimits.MaxResourcesPerPlugin; i++)
        {
            Assert.True(PluginResourceId.TryParse("plugin:example.smoke/r" + i, out var next));
            Assert.True(store.Publish(ReadyRequest(next, revision: 1)).IsOk);
        }

        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/extra", out var extra));
        var ninth = store.Publish(ReadyRequest(extra, revision: 1));
        Assert.False(ninth.IsOk);
        Assert.Equal(PluginError.ResourceFull, ninth.Error.Code);
    }

    [Fact]
    public void Server_cap_rejects_the_sixty_fifth_resource()
    {
        var store = new PluginResourceStore();
        for (var i = 0; i < PluginResourceLimits.MaxResourcesPerServer; i++)
        {
            Assert.True(PluginResourceId.TryParse("plugin:example.c" + i + "/queue", out var id));
            Assert.True(store.Publish(ReadyRequest(id, revision: 1)).IsOk);
        }

        Assert.True(PluginResourceId.TryParse("plugin:example.overflow/queue", out var extra));
        var overflow = store.Publish(ReadyRequest(extra, revision: 1));
        Assert.False(overflow.IsOk);
        Assert.Equal(PluginError.ResourceFull, overflow.Error.Code);
    }

    [Fact]
    public void Oversized_publish_is_rejected_whole()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var result = store.Publish(ReadyRequest(id, revision: 1) with
        {
            PublishBytes = PluginResourceLimits.MaxPublishBytes + 1,
        });
        Assert.False(result.IsOk);
        Assert.Equal(PluginError.ResourceFull, result.Error.Code);
    }

    [Fact]
    public void Tokens_strip_control_characters_and_cap_at_80()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var longValue = new string('x', 120);
        var published = store.Publish(new PluginResourcePublishRequest
        {
            Id = id,
            Schema = PluginResourceLimits.CollectionSchema,
            Revision = 1,
            Value = new PluginCollectionValue
            {
                Summary = "hi\nthere",
                Items =
                [
                    new PluginCollectionItem
                    {
                        Id = "work-42",
                        Status = "idle",
                        Attention = 0,
                        Tokens = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["title"] = "ab\u0001c" + longValue,
                        },
                    },
                ],
            },
        });
        Assert.True(published.IsOk, published.IsOk ? "" : published.Error.Message);
        var item = Assert.Single(published.Value.Resource.Value.Items);
        Assert.Equal("hithere", published.Value.Resource.Value.Summary);
        Assert.Equal("abc" + new string('x', MetadataTokenLimits.MaxValueLength - 3), item.Tokens["title"]);
        Assert.Equal(MetadataTokenLimits.MaxValueLength, item.Tokens["title"].Length);
    }

    [Fact]
    public void Tokens_record_seq_and_reject_ttl_outside_range()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        var published = store.Publish(ReadyRequest(id, revision: 1) with
        {
            Value = new PluginCollectionValue
            {
                Items =
                [
                    new PluginCollectionItem
                    {
                        Id = "work-42",
                        Status = "idle",
                        Attention = 0,
                        Sequence = 9,
                        TtlMs = 1000,
                        Tokens = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["title"] = "Plan retry policy",
                        },
                    },
                ],
            },
        });
        Assert.True(published.IsOk, published.IsOk ? "" : published.Error.Message);
        var item = Assert.Single(published.Value.Resource.Value.Items);
        Assert.Equal(9, item.Sequence);
        Assert.Equal(1000, item.TtlMs);

        var zeroTtl = store.Publish(ReadyRequest(id, revision: 2) with
        {
            Value = new PluginCollectionValue
            {
                Items =
                [
                    new PluginCollectionItem
                    {
                        Id = "work-42",
                        Status = "idle",
                        Attention = 0,
                        TtlMs = 0,
                    },
                ],
            },
        });
        Assert.False(zeroTtl.IsOk);
        Assert.Equal(PluginError.ResourceMalformed, zeroTtl.Error.Code);
        Assert.Equal(1, store.Get(id.Value).Value.Revision);

        var hugeTtl = store.Publish(ReadyRequest(id, revision: 2) with
        {
            Value = new PluginCollectionValue
            {
                Items =
                [
                    new PluginCollectionItem
                    {
                        Id = "work-42",
                        Status = "idle",
                        Attention = 0,
                        TtlMs = MetadataTokenLimits.MaxTtlMilliseconds + 1,
                    },
                ],
            },
        });
        Assert.False(hugeTtl.IsOk);
        Assert.Equal(PluginError.ResourceMalformed, hugeTtl.Error.Code);
        Assert.Equal(1, store.Get(id.Value).Value.Revision);
    }

    [Fact]
    public void Remove_outside_caller_prefix_is_source_denied()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        Assert.True(store.Publish(ReadyRequest(id, revision: 1)).IsOk);
        var denied = store.Remove(id.Value, callerPluginId: "other.plugin");
        Assert.False(denied.IsOk);
        Assert.Equal(PluginError.SourceDenied, denied.Error.Code);
        Assert.True(store.Get(id.Value).IsOk);
    }

    [Fact]
    public void Drop_plugin_removes_only_that_plugin()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var a));
        Assert.True(PluginResourceId.TryParse("plugin:other.plugin/queue", out var b));
        Assert.True(store.Publish(ReadyRequest(a, revision: 1)).IsOk);
        Assert.True(store.Publish(ReadyRequest(b, revision: 1)).IsOk);
        var dropped = store.DropPlugin("example.smoke");
        Assert.Equal(a.Value, Assert.Single(dropped).Id.Value);
        Assert.False(store.Get(a.Value).IsOk);
        Assert.True(store.Get(b.Value).IsOk);
    }

    [Fact]
    public void Expired_resource_reads_as_stale()
    {
        var store = new PluginResourceStore();
        Assert.True(PluginResourceId.TryParse("plugin:example.smoke/queue", out var id));
        Assert.True(store.Publish(ReadyRequest(id, revision: 1) with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        }).IsOk);
        Assert.Equal(PluginResourceLimits.FreshnessStale, store.Get(id.Value).Value.Freshness);
    }

    [Fact]
    public void Mark_stale_without_prior_publish_is_unavailable()
    {
        var store = new PluginResourceStore();
        var marked = store.MarkStale("plugin:example.smoke/queue");
        Assert.True(marked.IsOk);
        Assert.Equal(PluginResourceLimits.FreshnessUnavailable, marked.Value.Freshness);
        Assert.Equal(0, marked.Value.Revision);
    }

    private static PluginResourcePublishRequest ReadyRequest(
        PluginResourceId id,
        long revision,
        string summary = "3 need attention") =>
        new()
        {
            Id = id,
            Schema = PluginResourceLimits.CollectionSchema,
            Revision = revision,
            Value = new PluginCollectionValue
            {
                Summary = summary,
                Items =
                [
                    new PluginCollectionItem
                    {
                        Id = "work-42",
                        Label = "Plan retry policy",
                        Status = "blocked",
                        Attention = 2,
                        Tokens = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["title"] = "Plan retry policy",
                            ["age"] = "12m",
                        },
                    },
                ],
            },
        };
}
