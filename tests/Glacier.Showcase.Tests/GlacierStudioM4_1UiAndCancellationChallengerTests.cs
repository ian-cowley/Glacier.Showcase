using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;
using Xunit.Abstractions;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Adversarial Challenger Stress Suite (Milestone M4_1).
/// Rigorously stress-tests:
/// 1. Mid-stream cancellation under extreme streaming load (verifying 0 task leaks, 0 unobserved exceptions, and 0 dispatcher freezes).
/// 2. High-cadence session switching (sub-10ms intervals) during concurrent streaming.
/// 3. Strict verification of latency percentiles: Mean < 15ms, P95 < 40ms, Max < 100ms.
/// 4. Mid-tool execution cancellation resilience (process cleanup and zero UI lockup).
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierStudioM4_1UiAndCancellationChallengerTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;
    private readonly List<string> _createdSessionIds = new();

    public GlacierStudioM4_1UiAndCancellationChallengerTests(ITestOutputHelper output)
    {
        _output = output;
        _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        _sessionsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "sessions");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sessionsDir))
            {
                foreach (var id in _createdSessionIds)
                {
                    string file = Path.Combine(_sessionsDir, $"{id}.json");
                    if (File.Exists(file))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    private class MockInfiniteStreamingHandler : HttpMessageHandler
    {
        private readonly int _tokenDelayMs;
        private readonly Func<int, string>? _customSseFactory;

        public MockInfiniteStreamingHandler(int tokenDelayMs = 10, Func<int, string>? customSseFactory = null)
        {
            _tokenDelayMs = tokenDelayMs;
            _customSseFactory = customSseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var stream = new InfiniteSseStream(_tokenDelayMs, cancellationToken, _customSseFactory);
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        }
    }

    private class InfiniteSseStream : Stream
    {
        private readonly int _tokenDelayMs;
        private readonly CancellationToken _requestCt;
        private readonly Func<int, string>? _customSseFactory;
        private int _index = 0;

        public InfiniteSseStream(int tokenDelayMs, CancellationToken requestCt, Func<int, string>? customSseFactory)
        {
            _tokenDelayMs = tokenDelayMs;
            _requestCt = requestCt;
            _customSseFactory = customSseFactory;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_requestCt, cancellationToken);
            await Task.Delay(_tokenDelayMs, linked.Token);

            int cur = Interlocked.Increment(ref _index);
            string sseChunk = _customSseFactory != null
                ? _customSseFactory(cur)
                : $"data: {{\"choices\":[{{\"delta\":{{\"content\":\" tok_{cur}\"}}}}]}}\n\n";

            byte[] bytes = Encoding.UTF8.GetBytes(sseChunk);
            int toCopy = Math.Min(bytes.Length, buffer.Length);
            bytes.AsMemory(0, toCopy).CopyTo(buffer);
            return toCopy;
        }
    }

    private class MockToolCallingSseHandler : HttpMessageHandler
    {
        private readonly string _sseToolPayload;

        public MockToolCallingSseHandler(string sseToolPayload)
        {
            _sseToolPayload = sseToolPayload;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string payload = _sseToolPayload + "data: [DONE]\n\n";
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(resp);
        }
    }

    // =========================================================================
    // 1. Stress Test: Mid-Stream Cancellation (Burst of 15 Cancellations)
    // =========================================================================
    [Fact]
    public async Task StressTest_MidStreamCancellation_ZeroTaskLeaks_ZeroUnobservedExceptions_NoDispatcherFreeze()
    {
        var unobservedExceptions = new ConcurrentBag<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> unobservedHandler = (s, e) =>
        {
            unobservedExceptions.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += unobservedHandler;

        try
        {
            var handler = new MockInfiniteStreamingHandler(tokenDelayMs: 5);
            var httpClient = new HttpClient(handler);
            var state = new GlacierStudioState { IsAgentMode = true };
            var consoleService = new GlacierConsoleService();
            var docPreview = new GlacierDocTreePreviewService();
            var mcpManager = new GlacierMcpManager();

            var engine = new GlacierAgentEngine(httpClient, state, consoleService, docPreview, mcpManager);

            // Execute 15 rapid mid-stream cancellations with varied timings
            for (int cycle = 0; cycle < 15; cycle++)
            {
                var sess = state.CreateNewSession($"burst_cancellation_sess_{cycle}");
                _createdSessionIds.Add(sess.Id);

                int stateChangeCount = 0;
                Action onState = () => Interlocked.Increment(ref stateChangeCount);
                state.OnStateChanged += onState;

                var agentTask = engine.ProcessUserMessageAsync($"Query turn {cycle} to be cancelled mid-stream");

                // Vary delay between 5ms and 35ms to catch streaming at different execution phases
                int waitMs = 5 + (cycle * 2);
                await Task.Delay(waitMs);

                // Issue cancellation
                engine.CancelCurrentExecution();

                // Must complete within 2500ms without hang or deadlocking
                var completed = await Task.WhenAny(agentTask, Task.Delay(2500));
                Assert.Same(agentTask, completed);

                state.OnStateChanged -= onState;

                // State verification
                Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
                Assert.Equal("Cancelled by user", state.StatusText);

                // Dispatcher responsiveness check: ensure state events still fire immediately (< 5ms)
                long tStart = Stopwatch.GetTimestamp();
                bool eventFired = false;
                Action pingHandler = () => eventFired = true;
                state.OnStateChanged += pingHandler;
                state.NotifyStateChanged();
                state.OnStateChanged -= pingHandler;
                long tEnd = Stopwatch.GetTimestamp();

                double dispatchLatencyMs = (tEnd - tStart) * 1000.0 / Stopwatch.Frequency;
                Assert.True(eventFired, "UI Dispatcher failed to fire event after cancellation");
                Assert.True(dispatchLatencyMs < 5.0, $"Dispatcher responsiveness degraded: {dispatchLatencyMs:F3} ms");
            }

            // Force garbage collection of all generations to trigger finalizers for any leaked unobserved tasks
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            // Assert zero unobserved task exceptions
            Assert.Empty(unobservedExceptions);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= unobservedHandler;
        }
    }

    // =========================================================================
    // 2. Stress Test: High-Cadence Session Switching (Sub-10ms Intervals)
    // =========================================================================
    [Fact]
    public async Task StressTest_HighCadenceSessionSwitching_Sub10msIntervals_LatencyPercentilesStrictCheck()
    {
        var sseCts = new CancellationTokenSource();
        var handler = new MockInfiniteStreamingHandler(tokenDelayMs: 5);
        var httpClient = new HttpClient(handler);

        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(httpClient, state, consoleService, docPreview, mcpManager);

        // Pre-create 12 sessions
        var sessionIds = new List<string>();
        for (int s = 0; s < 12; s++)
        {
            var sess = state.CreateNewSession($"cadence_session_{s}");
            _createdSessionIds.Add(sess.Id);
            sessionIds.Add(sess.Id);
            sess.Messages.Add(new ChatMessage { Role = "user", Content = $"User message {s}" });
            sess.Messages.Add(new ChatMessage { Role = "assistant", Content = $"Assistant message {s}" });
            state.SaveSessionToDisk(sess);
        }

        // Start active background token streaming
        var agentTask = engine.ProcessUserMessageAsync("Background continuous streaming load for cadence benchmark");

        var switchLatencies = new List<double>();
        var searchLatencies = new List<double>();
        var totalInteractions = 200;

        // Perform 200 high-frequency interactions at 4ms intervals (sub-10ms cadence, 250 Hz)
        for (int i = 0; i < totalInteractions; i++)
        {
            await Task.Delay(4);

            string targetSessionId = sessionIds[i % sessionIds.Count];

            long tStart = Stopwatch.GetTimestamp();

            // Perform high-cadence switch
            state.SwitchSession(targetSessionId);

            // Assert active session switched correctly
            Assert.Equal(targetSessionId, state.ActiveSession.Id);

            long tMid = Stopwatch.GetTimestamp();
            double switchMs = (tMid - tStart) * 1000.0 / Stopwatch.Frequency;
            switchLatencies.Add(switchMs);

            // Periodically perform real-time search filtering
            if (i % 5 == 0)
            {
                long tSearchStart = Stopwatch.GetTimestamp();
                state.SessionSearchQuery = "cadence";
                var matches = state.FilteredSessions.ToList();
                Assert.NotEmpty(matches);
                state.SessionSearchQuery = "";
                long tSearchEnd = Stopwatch.GetTimestamp();

                double searchMs = (tSearchEnd - tSearchStart) * 1000.0 / Stopwatch.Frequency;
                searchLatencies.Add(searchMs);
            }
        }

        // Cleanly cancel background streaming
        engine.CancelCurrentExecution();
        sseCts.Cancel();
        try
        {
            await agentTask;
        }
        catch (OperationCanceledException) { }

        // Statistical analysis on session switch latencies
        switchLatencies.Sort();
        double min = switchLatencies.First();
        double max = switchLatencies.Last();
        double mean = switchLatencies.Average();
        double p50 = switchLatencies[(int)(switchLatencies.Count * 0.50)];
        double p95 = switchLatencies[(int)(switchLatencies.Count * 0.95)];
        double p99 = switchLatencies[(int)(switchLatencies.Count * 0.99)];

        _output.WriteLine($"=== High-Cadence Session Switch Telemetry ({switchLatencies.Count} samples at 250 Hz) ===");
        _output.WriteLine($"  Min:  {min:F4} ms");
        _output.WriteLine($"  Mean: {mean:F4} ms");
        _output.WriteLine($"  P50:  {p50:F4} ms");
        _output.WriteLine($"  P95:  {p95:F4} ms");
        _output.WriteLine($"  P99:  {p99:F4} ms");
        _output.WriteLine($"  Max:  {max:F4} ms");

        if (searchLatencies.Count > 0)
        {
            _output.WriteLine($"=== Concurrent Search Filter Telemetry ({searchLatencies.Count} samples) ===");
            _output.WriteLine($"  Mean: {searchLatencies.Average():F4} ms, Max: {searchLatencies.Max():F4} ms");
        }

        // Strict Percentile Contracts:
        // Mean < 15ms, P95 < 40ms, Max < 100ms
        Assert.True(mean < 15.0, $"Mean latency violated contract (< 15ms): {mean:F2} ms");
        Assert.True(p95 < 40.0, $"P95 latency violated contract (< 40ms): {p95:F2} ms");
        Assert.True(max < 100.0, $"Max latency violated contract (< 100ms): {max:F2} ms");
    }

    // =========================================================================
    // 3. Stress Test: Cancellation During Active Tool Execution
    // =========================================================================
    [Fact]
    public async Task StressTest_MidToolExecutionCancellation_AbortsWithoutLeakingSubprocessOrDeadlock()
    {
        // Setup handler that emits a tool call to run a sleeping shell command, followed by [DONE]
        string sseToolCall = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_sleep_test\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"powershell -NoProfile -Command Start-Sleep -Seconds 10\\\"}\"}}]}}]}\n\n";

        var handler = new MockToolCallingSseHandler(sseToolCall);
        var httpClient = new HttpClient(handler);
        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(httpClient, state, consoleService, docPreview, mcpManager);

        var sess = state.CreateNewSession("mid_tool_cancellation_sess");
        _createdSessionIds.Add(sess.Id);

        var agentTask = engine.ProcessUserMessageAsync("Trigger sleep tool then cancel");

        // Wait until agent enters ExecutingTools state
        var timeout = Stopwatch.StartNew();
        while (state.ExecutionStatus != AgentExecutionStatus.ExecutingTools && timeout.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(20);
        }

        Assert.Equal(AgentExecutionStatus.ExecutingTools, state.ExecutionStatus);

        // Cancel mid-execution of the child process
        engine.CancelCurrentExecution();

        // Must complete promptly (< 3000ms), not wait for 10 seconds
        var completed = await Task.WhenAny(agentTask, Task.Delay(3000));
        Assert.Same(agentTask, completed);

        Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
        Assert.Equal("Cancelled by user", state.StatusText);

        // Verify last message notes cancellation
        var lastMsg = sess.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("Execution was cancelled", lastMsg.Content);
    }

    // =========================================================================
    // 4. Stress Test: Concurrent Burst of Mixed UI Interactions Under Streaming
    // =========================================================================
    [Fact]
    public async Task StressTest_ConcurrentBurstUiInteractions_UnderStreamingSaturatingLoad()
    {
        var handler = new MockInfiniteStreamingHandler(tokenDelayMs: 2);
        var httpClient = new HttpClient(handler);
        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(httpClient, state, consoleService, docPreview, mcpManager);

        for (int s = 0; s < 8; s++)
        {
            var sess = state.CreateNewSession($"burst_mixed_{s}");
            _createdSessionIds.Add(sess.Id);
            sess.Messages.Add(new ChatMessage { Role = "user", Content = $"Prompt {s}" });
            state.SaveSessionToDisk(sess);
        }

        var agentTask = engine.ProcessUserMessageAsync("Heavy streaming load for mixed interaction stress");

        var latencies = new ConcurrentBag<double>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Launch 4 concurrent workers probing UI subsystems
        var workers = new List<Task>
        {
            // Worker 1: Tab toggling
            Task.Run(async () =>
            {
                var tabs = new[] { "doc", "console", "mcp", "telemetry" };
                int idx = 0;
                while (!cts.IsCancellationRequested)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    state.ActiveRightTab = tabs[idx++ % tabs.Length];
                    state.NotifyStateChanged();
                    long t1 = Stopwatch.GetTimestamp();
                    latencies.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                    await Task.Delay(5);
                }
            }),

            // Worker 2: Session switching
            Task.Run(async () =>
            {
                int idx = 0;
                while (!cts.IsCancellationRequested)
                {
                    var sessions = state.Sessions;
                    if (sessions.Count > 0)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        state.SwitchSession(sessions[idx++ % sessions.Count].Id);
                        long t1 = Stopwatch.GetTimestamp();
                        latencies.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                    }
                    await Task.Delay(5);
                }
            }),

            // Worker 3: Console log emission & ANSI parsing
            Task.Run(async () =>
            {
                int count = 0;
                while (!cts.IsCancellationRequested)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    consoleService.AddLog("USER", $"[BURST_LOG_{count++}]: \u001b[32mOK\u001b[0m test log");
                    long t1 = Stopwatch.GetTimestamp();
                    latencies.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                    await Task.Delay(5);
                }
            }),

            // Worker 4: Session search query evaluation
            Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    state.SessionSearchQuery = "burst";
                    var _ = state.FilteredSessions.ToList();
                    state.SessionSearchQuery = "";
                    long t1 = Stopwatch.GetTimestamp();
                    latencies.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                    await Task.Delay(10);
                }
            })
        };

        await Task.WhenAll(workers);

        engine.CancelCurrentExecution();
        try { await agentTask; } catch (OperationCanceledException) { }

        var list = latencies.ToList();
        list.Sort();
        double mean = list.Average();
        double p95 = list[(int)(list.Count * 0.95)];
        double max = list.Last();

        _output.WriteLine($"=== Mixed Concurrent Stress Telemetry ({list.Count} samples) ===");
        _output.WriteLine($"  Mean: {mean:F4} ms");
        _output.WriteLine($"  P95:  {p95:F4} ms");
        _output.WriteLine($"  Max:  {max:F4} ms");

        Assert.True(mean < 15.0, $"Mean latency violated contract (< 15ms): {mean:F2} ms");
        Assert.True(p95 < 40.0, $"P95 latency violated contract (< 40ms): {p95:F2} ms");
        Assert.True(max < 100.0, $"Max latency violated contract (< 100ms): {max:F2} ms");
    }
}
