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
/// Adversarial stress tests for Milestone M3:
/// Circuit Breaker State Transitions & Dual-Tier Failover Resilience in GlacierAgentEngine.
/// Authored by Empirical Challenger 1.
/// </summary>
public class GlacierCircuitBreakerAdversarialTests
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

    #region 1. Circuit Breaker State Machine Adversarial Stress Tests

    [Fact]
    public void CircuitBreaker_IntermittentFailures_ResetOnSuccess_DoesNotTrip()
    {
        using var launcher = CreateTestLauncher();

        // 2 failures, then 1 success
        launcher.RecordRemoteFailure("Intermittent failure 1");
        launcher.RecordRemoteFailure("Intermittent failure 2");
        Assert.Equal(2, launcher.RemoteConsecutiveFailures);
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);

        launcher.RecordRemoteSuccess(10);
        Assert.Equal(0, launcher.RemoteConsecutiveFailures);
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);

        // 2 more failures -> still under 3 consecutive -> remains Closed
        launcher.RecordRemoteFailure("Intermittent failure 3");
        launcher.RecordRemoteFailure("Intermittent failure 4");
        Assert.Equal(2, launcher.RemoteConsecutiveFailures);
        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
    }

    [Fact]
    public void CircuitBreaker_ExactThreeConsecutiveFailures_TripsToOpen_SwitchesActiveEndpoint()
    {
        using var launcher = CreateTestLauncher();

        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);
        Assert.True(launcher.IsCircuitBreakerOpen);
        Assert.Equal(3, launcher.RemoteConsecutiveFailures);
        Assert.Equal(3, launcher.RemoteErrorCount);
        // Active endpoint switches to local standalone
        Assert.Equal($"http://127.0.0.1:{launcher.InferencePort}/v1", launcher.ActiveEndpoint);
    }

    [Fact]
    public void CircuitBreaker_OpenState_BeforeCooldownExpiry_RemainsOpen()
    {
        using var launcher = CreateTestLauncher();
        launcher.CircuitBreakerCooldown = TimeSpan.FromSeconds(15);

        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);

        // Immediately checking state should still be Open
        Assert.Equal(CircuitBreakerState.Open, launcher.GetCurrentCircuitState());
        Assert.True(launcher.IsCircuitBreakerOpen);
    }

    [Fact]
    public async Task CircuitBreaker_OpenState_AfterCooldownExpiry_TransitionsToHalfOpen()
    {
        using var launcher = CreateTestLauncher();
        // Set short cooldown for empirical verification
        launcher.CircuitBreakerCooldown = TimeSpan.FromMilliseconds(40);

        launcher.RecordRemoteFailure("Fail 1");
        launcher.RecordRemoteFailure("Fail 2");
        launcher.RecordRemoteFailure("Fail 3");
        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);

        // Wait past cooldown
        await Task.Delay(60);

        Assert.Equal(CircuitBreakerState.HalfOpen, launcher.RemoteNodeState);
        Assert.Equal(CircuitBreakerState.HalfOpen, launcher.GetCurrentCircuitState());
    }

    [Fact]
    public void CircuitBreaker_HalfOpen_ProbeFailure_ImmediatelyTripsBackToOpenWithNewTimestamp()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetCircuitStateForTesting(CircuitBreakerState.HalfOpen);
        DateTime beforeTrip = DateTime.UtcNow;

        launcher.RecordRemoteFailure("Probe failure in half-open state");

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);
        Assert.True(launcher.IsCircuitBreakerOpen);
        Assert.True(launcher.LastCircuitBreakerTripTime >= beforeTrip);
    }

    [Fact]
    public void CircuitBreaker_HalfOpen_ProbeSuccess_RestoresClosedStateAndClearsFailures()
    {
        using var launcher = CreateTestLauncher();
        launcher.SetCircuitStateForTesting(CircuitBreakerState.HalfOpen);

        launcher.RecordRemoteSuccess(18);

        Assert.Equal(CircuitBreakerState.Closed, launcher.RemoteNodeState);
        Assert.Equal(0, launcher.RemoteConsecutiveFailures);
        Assert.False(launcher.IsCircuitBreakerOpen);
        Assert.Equal(18, launcher.BeastClusterLatencyMs);
        Assert.Equal(RuntimeHealthState.Online, launcher.BeastClusterState);
    }

    [Fact]
    public void CircuitBreaker_TelemetryHistory_RingBufferCapsAt100Items()
    {
        using var launcher = CreateTestLauncher();

        // Record 150 events
        for (int i = 0; i < 150; i++)
        {
            if (i % 2 == 0)
                launcher.RecordRemoteSuccess(10 + i);
            else
                launcher.RecordRemoteFailure($"Failure {i}");
        }

        Assert.Equal(100, launcher.TelemetryHistory.Count);
        // Ensure newest events are retained
        Assert.True(launcher.TelemetryHistory.Last().Success || launcher.TelemetryHistory.Last().ErrorMessage != null);
    }

    [Fact]
    public void CircuitBreaker_ConcurrentFailureRecording_MaintainsConsistency()
    {
        using var launcher = CreateTestLauncher();

        // Run 50 concurrent failure records
        Parallel.For(0, 50, i =>
        {
            launcher.RecordRemoteFailure($"Concurrent failure {i}");
        });

        Assert.Equal(CircuitBreakerState.Open, launcher.RemoteNodeState);
        Assert.True(launcher.RemoteConsecutiveFailures >= 3);
        Assert.Equal(50, launcher.RemoteErrorCount);
        Assert.True(launcher.TelemetryHistory.Count <= 100);
    }

    #endregion

    #region 2. Adversarial Agent Loop Resilience & Fallback Tests

    [Fact]
    public async Task AgentLoop_RemoteBeastReturns500InternalServerError_SeamlesslyFallsBackToLocal()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            if (url.Contains("192.168.1.108"))
            {
                // Beast returns HTTP 500 Internal Server Error
                var errorResp = new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("{\"error\":\"CUDA Out of Memory during prefill on Beast node\"}", Encoding.UTF8, "application/json")
                };
                return Task.FromResult(errorResp);
            }

            // Local fallback endpoint succeeds
            var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Processed safely on local RTX 4060.\"}}]}\n\ndata: [DONE]\n\n";
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

        await agentEngine.ProcessUserMessageAsync("Analyze cluster performance.");

        // Assert Beast was attempted first, then fallback to local
        Assert.Contains(visitedEndpoints, u => u.Contains("192.168.1.108"));
        Assert.Contains(visitedEndpoints, u => u.Contains("127.0.0.1:5055"));

        // Assistant message received content from local fallback
        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Processed safely on local RTX 4060", lastMsg.Content);
        Assert.Contains("Beast cluster node unavailable", lastMsg.Content);
        Assert.False(lastMsg.IsError);

        // Remote failure recorded
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
    }

    [Fact]
    public async Task AgentLoop_RemoteBeastReturns503ServiceUnavailable_SeamlesslyFallsBackToLocal()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            if (url.Contains("192.168.1.108"))
            {
                // Beast returns HTTP 503
                var errorResp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("Service Unavailable: Node busy", Encoding.UTF8, "text/plain")
                };
                return Task.FromResult(errorResp);
            }

            // Local fallback endpoint succeeds
            var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Processed on local fallback after 503.\"}}]}\n\ndata: [DONE]\n\n";
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

        await agentEngine.ProcessUserMessageAsync("Test 503 fallback.");

        Assert.Contains(visitedEndpoints, u => u.Contains("192.168.1.108"));
        Assert.Contains(visitedEndpoints, u => u.Contains("127.0.0.1:5055"));

        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Processed on local fallback after 503", lastMsg.Content);
        Assert.False(lastMsg.IsError);
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
    }

    [Fact]
    public async Task AgentLoop_RemoteBeastNetworkTimeout_SeamlesslyFallsBackToLocal()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            if (url.Contains("192.168.1.108"))
            {
                // Simulate network timeout or socket exception
                throw new HttpRequestException("Connection timed out after 3000ms", new TimeoutException());
            }

            var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Processed on local fallback after network timeout.\"}}]}\n\ndata: [DONE]\n\n";
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

        await agentEngine.ProcessUserMessageAsync("Test timeout fallback.");

        Assert.Contains(visitedEndpoints, u => u.Contains("192.168.1.108"));
        Assert.Contains(visitedEndpoints, u => u.Contains("127.0.0.1:5055"));

        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Processed on local fallback after network timeout", lastMsg.Content);
        Assert.False(lastMsg.IsError);
        Assert.Equal(1, launcher.RemoteConsecutiveFailures);
    }

    [Fact]
    public async Task AgentLoop_RemoteBeastMalformedJsonChunks_HandledGracefully()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            // Stream containing corrupted JSON chunks mixed with valid chunks
            var sseStream = new StringBuilder();
            sseStream.Append("data: {\"choices\":[{\"delta\":{\"content\":\"Token 1 \"}}]}\n\n");
            sseStream.Append("data: {\"choices\":[{\"delta\":{\"content\":BROKEN_JSON_MALFORMED_CHUNK\n\n");
            sseStream.Append("data: {\"corrupted_object\": true, invalid\n\n");
            sseStream.Append("data: {\"choices\":[{\"delta\":{\"content\":\"Token 2\"}}]}\n\n");
            sseStream.Append("data: [DONE]\n\n");

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseStream.ToString(), Encoding.UTF8, "text/event-stream")
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

        await agentEngine.ProcessUserMessageAsync("Test malformed chunks.");

        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        // Valid tokens are parsed while malformed chunks are skipped without crashing
        Assert.Contains("Token 1 Token 2", lastMsg.Content);
        Assert.False(lastMsg.IsError);
    }

    [Fact]
    public async Task AgentLoop_BothRemoteAndLocalFail_GracefullySetsErrorStateWithoutCrash()
    {
        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            // Both Beast and Local throw connection errors
            throw new HttpRequestException("Socket connection refused completely.");
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

        // Must complete without throwing an unhandled exception
        await agentEngine.ProcessUserMessageAsync("Test cascading total failure.");

        Assert.Equal(AgentExecutionStatus.Error, state.ExecutionStatus);
        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.True(lastMsg.IsError);
        Assert.Contains("Model Endpoint Error", lastMsg.Content);
    }

    [Fact]
    public async Task AgentLoop_RemoteBeastHttpClientTimeout_DemonstratesTaskCanceledExceptionBypass()
    {
        var visitedEndpoints = new List<string>();

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            visitedEndpoints.Add(url);

            if (url.Contains("192.168.1.108"))
            {
                // HttpClient times out, throwing TaskCanceledException with inner TimeoutException
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.", new TimeoutException());
            }

            // Local fallback endpoint succeeds
            var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Processed safely on local RTX 4060.\"}}]}\n\ndata: [DONE]\n\n";
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

        await agentEngine.ProcessUserMessageAsync("Test HttpClient timeout.");

        // If TaskCanceledException was caught by 'catch (OperationCanceledException) { throw; }',
        // it bypassed the fallback handler, marked status as Cancelled instead of falling back to local!
        bool didFallbackToLocal = visitedEndpoints.Any(u => u.Contains("127.0.0.1:5055"));
        Assert.True(didFallbackToLocal, "HttpClient.Timeout (TaskCanceledException) must trigger fallback to local standalone rather than being swallowed as a user cancellation!");
    }


    [Fact]
    public async Task AgentLoop_MultiTurnFailoverWithToolCalls_DeepClonePreventsNodeParentageException()
    {
        int requestCount = 0;

        var mockHandler = new MockHttpHandler((req, ct) =>
        {
            requestCount++;
            string url = req.RequestUri?.ToString() ?? "";

            if (url.Contains("192.168.1.108"))
            {
                // Turn 1 Beast fails
                throw new HttpRequestException("Beast network reset");
            }

            // Local fallback succeeds with tool call
            if (requestCount == 2)
            {
                var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"\",\"tool_calls\":[{\"index\":0,\"id\":\"call_fallback\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"dir\\\"}\"}}]}}]}\n\ndata: [DONE]\n\n";
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sseChunk, Encoding.UTF8, "text/event-stream")
                };
                return Task.FromResult(resp);
            }
            else
            {
                // Turn 2 Local succeeds with final text
                var sseChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"Final completion after tool execution.\"}}]}\n\ndata: [DONE]\n\n";
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

        // If DeepClone is missing, re-using messagesPayload throws InvalidOperationException: "The node already has a parent."
        await agentEngine.ProcessUserMessageAsync("Run directory tool with failover.");

        var lastMsg = state.ActiveSession.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Final completion after tool execution", lastMsg.Content);
        Assert.False(lastMsg.IsError);
    }

    #endregion
}
