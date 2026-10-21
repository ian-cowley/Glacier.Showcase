using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Comprehensive unit tests verifying Milestone M3 requirements:
/// 1. Hardware-aware model selection and VRAM budget filtering (MaxStandaloneVramBudgetGb = 4.8).
/// 2. Remote cluster dispatch to Beast desktop (192.168.1.108:8080) with 3-strike circuit breaker.
/// 3. Dual-tier routing in GlacierAgentEngine (local standalone for tool turns, Beast for reasoning with fallback).
/// </summary>
public class GlacierRuntimeLauncherServiceTests
{
    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }

    private static GlacierRuntimeLauncherService CreateTestLauncher(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? httpHandler = null)
    {
        var handler = new MockHttpHandler(httpHandler ?? ((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                data = new[]
                {
                    new { id = "qwen2.5-coder-14b-instruct-q3_k_m" }
                }
            });
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }));

        var client = new HttpClient(handler);
        var state = new GlacierStudioState();
        var console = new GlacierConsoleService();

        return new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: null,
            probeClient: client,
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);
    }

    #region 1. Hardware-Aware Model Selection & VRAM Budget Filtering

    [Fact]
    public void HardwareAwareSelection_MaxStandaloneVramBudget_Enforces4Point8GbLimit()
    {
        Assert.Equal(4.8, GlacierRuntimeLauncherService.MaxStandaloneVramBudgetGb);
    }

    [Fact]
    public void HardwareAwareSelection_FiltersModelsAboveVramBudget()
    {
        using var launcher = CreateTestLauncher();

        launcher.AddDiscoveredModel("Qwen3-4B-Instruct (2.5 GB)", @"D:\models\Qwen3-4B-Instruct.gguf", 2.49);
        launcher.AddDiscoveredModel("qwen2.5-coder-7b (4.7 GB)", @"D:\models\qwen2.5-coder-7b.gguf", 4.68);
        launcher.AddDiscoveredModel("DeepSeek-R1-Distill-Qwen-14B (9.0 GB)", @"D:\models\DeepSeek-R1-Distill-Qwen-14B.gguf", 8.98);
        launcher.AddDiscoveredModel("Glacier-Architect-Coder-14B-Grandmaster (8.8 GB)", @"D:\models\Glacier-Architect-Coder-14B-Grandmaster.gguf", 8.83);

        string bestModelPath = launcher.GetBestFittingLocalModel(4.8);
        var bestModelInfo = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.NotNull(bestModelInfo);
        Assert.True(bestModelInfo.SizeGb <= 4.8, $"Expected model size <= 4.8 GB, but got {bestModelInfo.SizeGb} GB");
        Assert.Contains("coder-7b", bestModelPath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("14B", bestModelPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HardwareAwareSelection_PriorityOrder_FavorsCoderAndSmallModels()
    {
        var modelCoder7B = new DiscoveredGgufModel("qwen2.5-coder-7b (4.7 GB)", @"D:\qwen2.5-coder-7b.gguf", 4.68);
        var modelQwen3_4B = new DiscoveredGgufModel("Qwen3-4B-Instruct (2.5 GB)", @"D:\Qwen3-4B-Instruct.gguf", 2.49);
        var modelGeneric7B = new DiscoveredGgufModel("Generic-7B (4.5 GB)", @"D:\generic-7b.gguf", 4.5);

        int prioCoder = GlacierRuntimeLauncherService.GetLocalStandalonePriority(modelCoder7B);
        int prioQwen3 = GlacierRuntimeLauncherService.GetLocalStandalonePriority(modelQwen3_4B);
        int prioGeneric = GlacierRuntimeLauncherService.GetLocalStandalonePriority(modelGeneric7B);

        Assert.True(prioCoder < prioQwen3, "qwen2.5-coder-7b should have higher priority than Qwen3-4B");
        Assert.True(prioQwen3 < prioGeneric, "Qwen3-4B should have higher priority than generic 7B");
    }

    [Fact]
    public void HardwareAwareSelection_NoModelsFittingBudget_ReturnsEmptyString()
    {
        using var launcher = CreateTestLauncher();

        launcher.AddDiscoveredModel("DeepSeek-R1-14B (9.0 GB)", @"D:\models\14b.gguf", 9.0);
        launcher.AddDiscoveredModel("Qwen-32B (20.0 GB)", @"D:\models\32b.gguf", 20.0);

        string bestModel = launcher.GetBestFittingLocalModel(4.8);
        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.Equal("", bestModel);
        Assert.Null(bestInfo);
    }

    #endregion

    #region 2. 3-Strike Circuit Breaker State Machine

    [Fact]
    public void CircuitBreaker_InitialState_IsClosed()
    {
        using var launcher = CreateTestLauncher();

        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(0, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);
        Assert.Equal(launcher.BeastEndpointUrl, launcher.ActiveEndpoint);
    }

    [Fact]
    public void CircuitBreaker_UnderThreeFailures_RemainsClosed()
    {
        using var launcher = CreateTestLauncher();

        launcher.RecordRemoteFailure("Network blip 1");
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);

        launcher.RecordRemoteFailure("Network blip 2");
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(2, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);
    }

    [Fact]
    public void CircuitBreaker_ThreeConsecutiveFailures_TripsToOpen()
    {
        using var launcher = CreateTestLauncher();

        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);
        Assert.Equal(3, launcher.RemoteConsecutiveFailures);
        Assert.True(launcher.IsCircuitBreakerOpen);
        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", launcher.ActiveEndpoint);
    }

    [Fact]
    public async Task CircuitBreaker_Cooldown_TransitionsToHalfOpen()
    {
        using var launcher = CreateTestLauncher();
        launcher.CircuitBreakerCooldown = TimeSpan.FromMilliseconds(50);

        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");
        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);

        await Task.Delay(75);

        Assert.Equal(CircuitBreakerState.HalfOpen, launcher.RemoteNodeState);
    }

    [Fact]
    public void CircuitBreaker_HalfOpenFailure_TripsBackToOpen()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetCircuitStateForTesting(CircuitBreakerState.HalfOpen);

        launcher.RecordRemoteFailure("Probe failure in half-open");

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);
        Assert.True(launcher.IsCircuitBreakerOpen);
    }

    [Fact]
    public void CircuitBreaker_HalfOpenSuccess_ResetsToClosed()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetCircuitStateForTesting(CircuitBreakerState.HalfOpen);

        launcher.RecordRemoteSuccess(12);

        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(0, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);
        Assert.Equal(12, launcher.BeastClusterLatencyMs);
        Assert.Equal(RuntimeHealthState.Online, launcher.BeastClusterState);
    }

    [Fact]
    public void CircuitBreaker_Reset_RestoresInitialClosedState()
    {
        using var launcher = CreateTestLauncher();
        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");
        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);

        launcher.ResetCircuitBreaker();

        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(0, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);
    }

    [Fact]
    public void CircuitBreaker_TelemetryHistory_TracksEvents()
    {
        using var launcher = CreateTestLauncher();

        launcher.RecordRemoteSuccess(15);
        launcher.RecordRemoteFailure("Socket reset");

        Assert.Equal(2, launcher.TelemetryHistory.Count);
        Assert.True(launcher.TelemetryHistory[0].Success);
        Assert.Equal(15, launcher.TelemetryHistory[0].LatencyMs);
        Assert.False(launcher.TelemetryHistory[1].Success);
        Assert.Equal("Socket reset", launcher.TelemetryHistory[1].ErrorMessage);
        Assert.Equal(1, launcher.RemoteErrorCount);
    }

    [Fact]
    public async Task CircuitBreaker_ProbeRemoteNode_Success_RecordsOnline()
    {
        using var launcher = CreateTestLauncher((req, ct) =>
        {
            var json = JsonSerializer.Serialize(new
            {
                data = new[] { new { id = "qwen2.5-coder-14b-test" } }
            });
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        });

        bool success = await launcher.ProbeRemoteNodeAsync();

        Assert.True(success);
        Assert.Equal(RuntimeHealthState.Online, launcher.BeastClusterState);
        Assert.Equal("qwen2.5-coder-14b-test", launcher.BeastClusterActiveModel);
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
    }

    [Fact]
    public async Task CircuitBreaker_ProbeRemoteNode_Failure_RecordsOffline()
    {
        using var launcher = CreateTestLauncher((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        });

        bool success = await launcher.ProbeRemoteNodeAsync();

        Assert.False(success);
        Assert.Equal(RuntimeHealthState.Offline, launcher.BeastClusterState);
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
    }

    #endregion

    #region 3. Dual-Tier Routing Policy

    [Fact]
    public void DualTierRouting_LocalFastTool_AlwaysResolvesLocalStandalone()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");

        string endpoint = launcher.ResolveEndpointForTurn(AgentTurnTier.LocalFastTool);

        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", endpoint);
    }

    [Fact]
    public void DualTierRouting_RemoteDeepReasoning_ResolvesBeastWhenOnlineAndCircuitClosed()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");
        launcher.SetCircuitStateForTesting(CircuitBreakerState.Closed);

        string endpoint = launcher.ResolveEndpointForTurn(AgentTurnTier.RemoteDeepReasoning);
        string model = launcher.ResolveModelForTurn(AgentTurnTier.RemoteDeepReasoning);

        Assert.Equal(launcher.BeastEndpointUrl, endpoint);
        Assert.Equal("qwen2.5-coder-14b", model);
    }

    [Fact]
    public void DualTierRouting_RemoteDeepReasoning_FallsBackToLocalWhenCircuitOpen()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");
        launcher.SetCircuitStateForTesting(CircuitBreakerState.Open);

        string endpoint = launcher.ResolveEndpointForTurn(AgentTurnTier.RemoteDeepReasoning);
        string model = launcher.ResolveModelForTurn(AgentTurnTier.RemoteDeepReasoning);

        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", endpoint);
        Assert.NotEqual("qwen2.5-coder-14b", model);
    }

    [Fact]
    public void DualTierRouting_RemoteDeepReasoning_FallsBackToLocalWhenBeastOffline()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Offline);
        launcher.SetCircuitStateForTesting(CircuitBreakerState.Closed);

        string endpoint = launcher.ResolveEndpointForTurn(AgentTurnTier.RemoteDeepReasoning);

        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", endpoint);
    }

    #endregion

    #region 4. Dual-Tier GlacierAgentEngine End-to-End Orchestration

    [Fact]
    public async Task DualTierAgentEngine_RoutesTurnsAccordingToTier()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            // Turn 1 returns a tool call
            if (visitedEndpoints.Count == 1)
            {
                var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"\",\"tool_calls\":[{\"index\":0,\"id\":\"call_001\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"dir\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n";
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sseChunk, Encoding.UTF8, "text/event-stream")
                };
                return Task.FromResult(resp);
            }
            else
            {
                // Turn 2 returns final text
                var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Completed execution cleanly.\"}}]}\n\ndata: [DONE]\n\n";
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sseChunk, Encoding.UTF8, "text/event-stream")
                };
                return Task.FromResult(resp);
            }
        });

        var httpClient = new HttpClient(mockHandler);
        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");
        launcher.SetCircuitStateForTesting(CircuitBreakerState.Closed);

        var agentEngine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreview,
            mcpManager,
            launcherService: launcher);

        await agentEngine.ProcessUserMessageAsync("Please check the directory contents.");

        Assert.True(visitedEndpoints.Count >= 2, $"Expected at least 2 turns dispatched, got {visitedEndpoints.Count}");
        // Turn 1 (Strategic reasoning) must route to Beast endpoint (192.168.1.108:8080)
        Assert.Contains("192.168.1.108:8080", visitedEndpoints[0]);
        // Turn 2 (Tool result evaluation / local execution) must route to Local standalone endpoint (127.0.0.1:5055)
        Assert.Contains("127.0.0.1:5055", visitedEndpoints[1]);
    }

    [Fact]
    public async Task DualTierAgentEngine_BeastFailure_SeamlesslyFailsOverToLocal()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            if (url.Contains("192.168.1.108"))
            {
                // Beast endpoint throws connection error
                throw new HttpRequestException("Connection refused (192.168.1.108:8080)");
            }

            // Local endpoint succeeds
            var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Recovered on local standalone.\"}}]}\n\ndata: [DONE]\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseChunk, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(resp);
        });

        var httpClient = new HttpClient(mockHandler);
        var state = new GlacierStudioState { IsAgentMode = false };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        using var launcher = CreateTestLauncher();
        launcher.SetBeastClusterStateForTesting(RuntimeHealthState.Online, "qwen2.5-coder-14b");
        launcher.SetCircuitStateForTesting(CircuitBreakerState.Closed);

        var agentEngine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreview,
            mcpManager,
            launcherService: launcher);

        await agentEngine.ProcessUserMessageAsync("Analyze architecture.");

        // Beast was tried first and failed, then failed over to local standalone
        Assert.Contains(visitedEndpoints, u => u.Contains("192.168.1.108:8080"));
        Assert.Contains(visitedEndpoints, u => u.Contains("127.0.0.1:5055"));

        // Session was preserved and contains the local fallback answer
        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Recovered on local standalone", lastMsg.Content);
        Assert.Contains("Beast cluster node unavailable", lastMsg.Content);

        // Circuit breaker recorded the failure
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
    }

    #endregion
}
