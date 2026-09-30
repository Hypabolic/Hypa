using System.Text;
using System.Text.Json;
using Hypa.AgentRuntime.Protocol;
using Hypa.AgentRuntime.Protocol.Envelopes;
using Hypa.AgentRuntime.Protocol.Json;
using Hypa.AgentRuntime.Protocol.Models;
using Xunit;

namespace Hypa.AgentRuntime.Tests;

public class ProtocolRequiredFieldTests
{
    [Fact]
    public void Request_missing_method_fails_validation()
    {
        var req = new RpcRequest { Id = "1", Method = "" };
        Assert.False(ProtocolEnvelopeValidator.TryValidateRequest(req, out var err));
        Assert.Contains("method", err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Event_missing_type_fails_validation()
    {
        var envelope = new RpcEvent { Event = "" };
        Assert.False(ProtocolEnvelopeValidator.TryValidateEvent(envelope, out var err));
        Assert.Contains("event", err, StringComparison.OrdinalIgnoreCase);

        var p = new RuntimeEventParams { Type = "" };
        Assert.False(ProtocolEnvelopeValidator.TryValidateRuntimeEventParams(p, out err));
        Assert.Contains("type", err, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"pane_id\":\"p_1\"}", false)]
    [InlineData("{\"pane_id\":\"p_1\",\"data\":\"DQ==\"}", true)]
    [InlineData("{\"pane_id\":\"p_1\",\"lease_id\":\"lease_1\",\"data\":\"DQ==\"}", true)]
    public void Pane_send_keys_requires_pane_and_data(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneSendKeys,
            doc.RootElement,
            out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"subscription_id\":\"sub_1\"}", false)]
    [InlineData("{\"subscription_id\":\"sub_1\",\"pane_ids\":[]}", true)]
    [InlineData("{\"subscription_id\":\"sub_1\",\"pane_ids\":[\"p_1\"]}", true)]
    public void Terminal_visible_set_requires_subscription_and_pane_ids(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.TerminalVisibleSet, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Fact]
    public void Terminal_visible_set_rejects_blank_pane_id()
    {
        Assert.False(ProtocolEnvelopeValidator.TryValidateTerminalVisibleSet(
            new TerminalVisibleSetParams { SubscriptionId = "sub_1", PaneIds = ["", "p_1"] },
            out var error));
        Assert.Contains("pane_id", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"subscription_id\":\"sub_1\"}", true)]
    public void Events_unsubscribe_requires_subscription_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.EventsUnsubscribe,
            doc.RootElement,
            out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"timeout_ms\":1000}", false)]
    [InlineData("{\"match_event\":{\"event\":\"pane_agent_status_changed\"}}", false)]
    [InlineData("{\"match_event\":{\"event\":\"pane_agent_status_changed\",\"pane_id\":\"p1\"}}", false)]
    [InlineData("{\"match_event\":{\"event\":\"pane_agent_status_changed\",\"pane_id\":\"p1\",\"agent_status\":\"blocked\"}}", true)]
    public void Events_wait_requires_match_event_fields(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.EventsWait,
            doc.RootElement,
            out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"lease_id\":\"lease_1\"}", true)]
    public void Lease_release_requires_lease_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.RuntimeLeaseRelease,
            doc.RootElement,
            out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"workspace_id\":\"w1\"}", true)]
    public void Tab_create_requires_workspace_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.TabCreate, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"pane_id\":\"p1\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"direction\":\"right\"}", true)]
    public void Pane_split_requires_pane_id_and_direction(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneSplit, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"workspace_id\":\"w1\"}", false)]
    [InlineData("{\"workspace_id\":\"w1\",\"root\":{\"type\":\"pane\"}}", true)]
    public void Layout_apply_requires_workspace_id_and_root(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.LayoutApply, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p1\"}", true)]
    public void Agent_start_requires_pane_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentStart, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData(ProtocolMethods.WorkspaceFocus, "{}", false)]
    [InlineData(ProtocolMethods.WorkspaceFocus, "{\"workspace_id\":\"w1\"}", true)]
    [InlineData(ProtocolMethods.WorkspaceRename, "{\"workspace_id\":\"w1\"}", false)]
    [InlineData(ProtocolMethods.WorkspaceRename, "{\"workspace_id\":\"w1\",\"label\":\"n\"}", true)]
    [InlineData(ProtocolMethods.WorkspaceClose, "{}", false)]
    [InlineData(ProtocolMethods.WorkspaceClose, "{\"workspace_id\":\"w1\"}", true)]
    public void Workspace_focus_rename_and_close_require_fields(string method, string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(method, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData(ProtocolMethods.WorkspaceMove, "{}", false)]
    [InlineData(ProtocolMethods.WorkspaceMove, "{\"workspace_id\":\"w1\"}", false)]
    [InlineData(ProtocolMethods.WorkspaceMove, "{\"workspace_id\":\"w1\",\"insert_index\":0}", true)]
    [InlineData(ProtocolMethods.WorkspaceMoveBlock, "{}", false)]
    [InlineData(ProtocolMethods.WorkspaceMoveBlock, "{\"workspace_ids\":[\"w1\"]}", true)]
    [InlineData(ProtocolMethods.WorkspaceReportMetadata, "{\"workspace_id\":\"w1\"}", false)]
    [InlineData(ProtocolMethods.WorkspaceReportMetadata, "{\"workspace_id\":\"w1\",\"source\":\"git\"}", false)]
    [InlineData(ProtocolMethods.WorkspaceReportMetadata, "{\"workspace_id\":\"w1\",\"source\":\"git\",\"tokens\":{\"k\":\"v\"}}", true)]
    public void Workspace_move_and_report_require_fields(string method, string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(method, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData(ProtocolMethods.PaneRename, "{}", false)]
    [InlineData(ProtocolMethods.PaneRename, "{\"pane_id\":\"p1\"}", true)]
    [InlineData(ProtocolMethods.PaneFocus, "{}", false)]
    [InlineData(ProtocolMethods.PaneFocus, "{\"pane_id\":\"p1\"}", true)]
    [InlineData(ProtocolMethods.PaneNeighbor, "{}", false)]
    [InlineData(ProtocolMethods.PaneNeighbor, "{\"direction\":\"right\"}", true)]
    [InlineData(ProtocolMethods.PaneInputSet, "{\"pane_id\":\"p1\"}", false)]
    [InlineData(ProtocolMethods.PaneInputSet, "{\"pane_id\":\"p1\",\"right_click\":\"hypa\"}", true)]
    [InlineData(ProtocolMethods.PaneSendInput, "{}", false)]
    [InlineData(ProtocolMethods.PaneSendInput, "{\"pane_id\":\"p1\"}", true)]
    public void Pane_rename_focus_neighbor_and_input_require_fields(string method, string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(method, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData(ProtocolMethods.TabGet, "{}", false)]
    [InlineData(ProtocolMethods.TabGet, "{\"tab_id\":\"t1\"}", true)]
    [InlineData(ProtocolMethods.TabFocus, "{}", false)]
    [InlineData(ProtocolMethods.TabFocus, "{\"tab_id\":\"t1\"}", true)]
    [InlineData(ProtocolMethods.TabRename, "{\"tab_id\":\"t1\"}", false)]
    [InlineData(ProtocolMethods.TabRename, "{\"tab_id\":\"t1\",\"label\":\"n\"}", true)]
    [InlineData(ProtocolMethods.TabMove, "{\"tab_id\":\"t1\"}", false)]
    [InlineData(ProtocolMethods.TabMove, "{\"tab_id\":\"t1\",\"index\":0}", true)]
    [InlineData(ProtocolMethods.TabClose, "{}", false)]
    [InlineData(ProtocolMethods.TabClose, "{\"tab_id\":\"t1\"}", true)]
    [InlineData(ProtocolMethods.TabClose, "{\"tab_id\":\"t1\",\"if_empty\":true}", true)]
    [InlineData(ProtocolMethods.PaneMove, "{\"pane_id\":\"p1\"}", false)]
    [InlineData(ProtocolMethods.PaneMove, "{\"pane_id\":\"p1\",\"destination\":\"tab\"}", true)]
    [InlineData(ProtocolMethods.PaneFocusDirection, "{}", false)]
    [InlineData(ProtocolMethods.PaneFocusDirection, "{\"direction\":\"right\"}", true)]
    public void Tab_and_pane_move_require_fields(string method, string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(method, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p1\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"direction\":\"right\"}", true)]
    [InlineData("{\"target_pane_id\":\"p2\"}", true)]
    public void Pane_swap_requires_direction_or_target(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneSwap, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"path\":[0],\"ratio\":0.5,\"lease_id\":\"lease_1\"}", false)]
    [InlineData("{\"tab_id\":\"t1\",\"ratio\":0.5,\"lease_id\":\"lease_1\"}", false)]
    [InlineData("{\"tab_id\":\"t1\",\"path\":[0],\"lease_id\":\"lease_1\"}", false)]
    [InlineData("{\"tab_id\":\"t1\",\"path\":[0],\"ratio\":0.5}", false)]
    [InlineData("{\"tab_id\":\"t1\",\"path\":[0],\"ratio\":0.5,\"lease_id\":\"lease_1\"}", true)]
    public void Layout_set_split_ratio_requires_tab_id_path_ratio_and_lease_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.LayoutSetSplitRatio, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p1\"}", false)]
    [InlineData("{\"lease_id\":\"lease_1\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"lease_id\":\"lease_1\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"occupant_token\":\"occ_ab\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"parent_capability\":\"par_cd\"}", true)]
    public void Pane_show_and_hide_require_pane_id_and_a_placement_credential(string paramsJson, bool expectedOk)
    {
        using var show = JsonDocument.Parse(paramsJson);
        Assert.Equal(
            expectedOk,
            ProtocolEnvelopeValidator.TryValidateMethodParams(
                ProtocolMethods.PaneShow, show.RootElement, out _));
        using var hide = JsonDocument.Parse(paramsJson);
        Assert.Equal(
            expectedOk,
            ProtocolEnvelopeValidator.TryValidateMethodParams(
                ProtocolMethods.PaneHide, hide.RootElement, out _));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"title\":\"override\"}", true)]
    public void Window_title_set_requires_title(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientWindowTitleSet, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"fg\":{\"r\":1,\"g\":2,\"b\":3}}", true)]
    [InlineData("{\"fg\":{\"r\":256,\"g\":0,\"b\":0}}", false)]
    [InlineData("{\"appearance\":\"purple\"}", false)]
    public void Host_theme_set_optional_fields_and_bounds(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Fact]
    public void Host_theme_set_rejects_palette_index_out_of_range()
    {
        using var high = JsonDocument.Parse("""{"palette":[{"i":256,"r":1,"g":2,"b":3}]}""");
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, high.RootElement, out var highError));
        Assert.Contains("0-255", highError, StringComparison.Ordinal);
        using var missingRgb = JsonDocument.Parse("""{"palette":[{"i":1}]}""");
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, missingRgb.RootElement, out _));
        using var notArray = JsonDocument.Parse("""{"palette":{"i":1,"r":1,"g":2,"b":3}}""");
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, notArray.RootElement, out _));
        using var ok = JsonDocument.Parse("""{"palette":[{"i":0,"r":1,"g":2,"b":3},{"i":255,"r":4,"g":5,"b":6}]}""");
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, ok.RootElement, out _));
        using var empty = JsonDocument.Parse("""{"palette":[]}""");
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, empty.RootElement, out _));
    }

    [Fact]
    public void Host_theme_set_rejects_palette_longer_than_bound()
    {
        using var ok = JsonDocument.Parse(PaletteArrayJson(HostThemeSetParams.MaxPaletteEntries));
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, ok.RootElement, out _));
        using var over = JsonDocument.Parse(PaletteArrayJson(HostThemeSetParams.MaxPaletteEntries + 1));
        Assert.False(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, over.RootElement, out var error));
        Assert.Contains(
            HostThemeSetParams.MaxPaletteEntries.ToString(),
            error,
            StringComparison.Ordinal);
        Assert.Equal(512, HostThemeSetParams.MaxPaletteEntries);
    }

    private static string PaletteArrayJson(int count)
    {
        var sb = new StringBuilder();
        sb.Append("{\"palette\":[");
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(',');
            var index = i % 256;
            sb.Append("{\"i\":").Append(index).Append(",\"r\":1,\"g\":2,\"b\":3}");
        }

        sb.Append("]}");
        return sb.ToString();
    }

    [Fact]
    public void Host_theme_set_missing_params_is_ok()
    {
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientHostThemeSet, parameters: null, out _));
    }

    [Fact]
    public void Window_title_clear_has_no_required_fields()
    {
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientWindowTitleClear, parameters: null, out _));
        using var doc = JsonDocument.Parse("{}");
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ClientWindowTitleClear, doc.RootElement, out _));
    }

    [Fact]
    public void Typed_params_missing_required_fields_fail()
    {
        Assert.False(ProtocolEnvelopeValidator.TryValidateTerminalObserve(
            new TerminalObserveParams { PaneId = "p_1" }, out _));
        Assert.False(ProtocolEnvelopeValidator.TryValidateTerminalControl(
            new TerminalControlParams { PaneId = "p_1", LeaseId = "l1" }, out _));
        Assert.False(ProtocolEnvelopeValidator.TryValidatePaneSendKeys(
            new PaneSendKeysParams { PaneId = "p_1", LeaseId = "l1" }, out _));
        Assert.False(ProtocolEnvelopeValidator.TryValidateAgentPrompt(
            new AgentPromptParams { PaneId = "p_1" }, out _));
    }

    [Fact]
    public void Deserialize_response_without_id_still_parses_but_request_method_required()
    {
        var json = """{"result":{"ok":true}}""";
        var resp = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.RpcResponse);
        Assert.NotNull(resp);
        Assert.Null(resp.Id);

        var badReq = JsonSerializer.Deserialize(
            """{"id":"1","params":{}}""",
            ProtocolJsonContext.Default.RpcRequest);
        Assert.NotNull(badReq);
        Assert.False(ProtocolEnvelopeValidator.TryValidateRequest(badReq, out _));
    }

    [Fact]
    public void Server_reload_config_allows_empty_or_missing_params()
    {
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerReloadConfig, null, out _));
        using var doc = JsonDocument.Parse("{}");
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerReloadConfig, doc.RootElement, out _));
    }

    [Fact]
    public void Server_agent_manifest_methods_allow_empty_or_missing_params()
    {
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerAgentManifests, null, out _));
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerReloadAgentManifests, null, out _));
        using var doc = JsonDocument.Parse("{}");
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerAgentManifests, doc.RootElement, out _));
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.ServerReloadAgentManifests, doc.RootElement, out _));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"state\":\"blocked\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"state\":\"blocked\",\"agent_session_id\":\"s1\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"state\":\"blocked\",\"agent_session_path\":\"/tmp/s\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"state\":\"blocked\",\"agent_session_id\":\"s1\",\"agent_session_path\":\"/tmp/s\"}", false)]
    public void Pane_report_agent_requires_fields(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneReportAgent, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"agent_session_id\":\"s1\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\",\"agent_session_path\":\"/tmp/s\"}", true)]
    public void Pane_report_agent_session_requires_id_or_path(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneReportAgentSession, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"tokens\":{\"name\":\"claude\"}}", true)]
    public void Pane_report_metadata_requires_fields(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneReportMetadata, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\"}", false)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\",\"agent\":\"claude\"}", true)]
    public void Pane_release_agent_requires_fields(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneReleaseAgent, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p1\"}", true)]
    [InlineData("{\"pane_id\":\"p1\",\"source\":\"git\"}", true)]
    public void Pane_clear_agent_authority_requires_pane_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.PaneClearAgentAuthority, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p_1\"}", true)]
    [InlineData("{\"agent_id\":\"reviewer\"}", true)]
    public void Agent_explain_requires_pane_or_agent_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentExplain, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"target\":\"claude\"}", true)]
    [InlineData("{\"target\":\"\"}", false)]
    [InlineData("{\"detected\":true}", true)]
    [InlineData("{\"detected\":true,\"target\":\"claude\"}", false)]
    [InlineData("{\"target\":\"claude\",\"plan\":true}", true)]
    [InlineData("{\"target\":\"claude\",\"plan\":false}", true)]
    [InlineData("{\"detected\":true,\"plan\":true}", true)]
    [InlineData("{\"target\":\"claude\",\"plan\":\"yes\"}", false)]
    public void Integration_install_requires_target(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.IntegrationInstall, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{\"target\":\"claude\"}", true)]
    [InlineData("{\"target\":\"claude\",\"plan\":false}", true)]
    [InlineData("{\"detected\":true}", true)]
    [InlineData("{\"target\":\"claude\",\"plan\":true}", false)]
    [InlineData("{\"detected\":true,\"plan\":true}", false)]
    public void Integration_uninstall_rejects_plan(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.IntegrationUninstall, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p_1\"}", true)]
    public void Agent_rename_requires_pane_or_agent_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentRename, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p_1\"}", true)]
    public void Agent_focus_requires_pane_or_agent_id(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentFocus, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"pane_id\":\"p_1\"}", false)]
    [InlineData("{\"pane_id\":\"p_1\",\"keys\":[\"up\"]}", true)]
    [InlineData("{\"agent_id\":\"reviewer\",\"keys\":[]}", true)]
    public void Agent_send_keys_requires_target_and_keys_array(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentSendKeys, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"source\":\"example.views\"}", true)]
    public void Agent_view_set_requires_source(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentViewSet, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("{\"source\":\"example.views\"}", true)]
    public void Agent_view_clear_allows_empty_or_source(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.AgentViewClear, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"target\":\"pi\"}", true)]
    [InlineData("{\"detected\":true}", true)]
    [InlineData("{\"detected\":true,\"target\":\"pi\"}", false)]
    public void Integration_uninstall_requires_target(string paramsJson, bool expectedOk)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        var ok = ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.IntegrationUninstall, doc.RootElement, out _);
        Assert.Equal(expectedOk, ok);
    }

    [Fact]
    public void Integration_list_allows_empty_params()
    {
        Assert.True(ProtocolEnvelopeValidator.TryValidateMethodParams(
            ProtocolMethods.IntegrationList, null, out _));
    }
}

