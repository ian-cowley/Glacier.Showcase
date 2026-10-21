using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;
using Xunit.Abstractions;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Empirical Benchmark Harness verifying sub-100ms UI responsiveness during
/// high-throughput inference streaming (100 tok/s) and active tool execution.
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierStudioUiResponsivenessBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;
    private readonly List<string> _createdSessionIds = new();

    public GlacierStudioUiResponsivenessBenchmarkTests(ITestOutputHelper output)
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

    [Fact]
    public async Task StudioUiResponsiveness_DuringHighRateStreamingAndToolExecution_MaintainsSub100msLatency()
    {
        // 1. Setup mock HTTP server emitting continuous SSE tokens at 100 tokens/sec
        // and interleaving tool execution turns.
        var sseCts = new CancellationTokenSource();
        var handler = new MockStreamingAndToolExecutionHttpHandler(sseCts.Token);
        var httpClient = new HttpClient(handler);

        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreview = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreview,
            mcpManager);

        // Populate multiple sessions to exercise real session switching and search filtering
        for (int s = 0; s < 5; s++)
        {
            var sess = state.CreateNewSession($"bench_session_{s}");
            _createdSessionIds.Add(sess.Id);
            sess.Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = $"Test benchmark prompt {s} with keyword search term."
            });
            sess.Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = $"Test benchmark response {s} with detailed technical explanation."
            });
            state.SaveSessionToDisk(sess);
        }

        // 2. Launch background agent execution simulating active streaming and tool turns
        var agentTask = engine.ProcessUserMessageAsync("Benchmark UI responsiveness under streaming and tool load.");

        // 3. Concurrently probe UI event latency across 4 major interaction categories
        var latencies = new List<double>();

        // Sample 120 user interactions over a 3-second streaming window at 40 Hz
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(25); // 40 Hz interaction cadence

            long tStart = Stopwatch.GetTimestamp();

            // Simulate distinct UI interactions:
            switch (i % 4)
            {
                case 0:
                    // Interaction 1: Right drawer tab switching
                    state.ActiveRightTab = (i % 2 == 0) ? "console" : "doc";
                    state.NotifyStateChanged();
                    break;

                case 1:
                    // Interaction 2: Session switching
                    string targetId = state.Sessions[i % state.Sessions.Count].Id;
                    state.SwitchSession(targetId);
                    break;

                case 2:
                    // Interaction 3: Real-time session search filtering
                    state.SessionSearchQuery = "bench";
                    var filtered = state.FilteredSessions.ToList();
                    Assert.NotEmpty(filtered);
                    state.SessionSearchQuery = "";
                    break;

                case 3:
                    // Interaction 4: Console log addition & ANSI parse
                    consoleService.AddLog("USER", $"[UI_EVENT_{i}]: Interaction event processed.");
                    break;
            }

            long tEnd = Stopwatch.GetTimestamp();
            double latencyMs = (tEnd - tStart) * 1000.0 / Stopwatch.Frequency;
            latencies.Add(latencyMs);
        }

        // Stop background stream and cancel agent execution
        engine.CancelCurrentExecution();
        sseCts.Cancel();
        try
        {
            await agentTask;
        }
        catch (OperationCanceledException)
        {
            // Expected upon cancellation
        }

        // 4. Calculate statistical metrics
        latencies.Sort();
        double min = latencies.First();
        double max = latencies.Last();
        double mean = latencies.Average();
        double p50 = latencies[(int)(latencies.Count * 0.50)];
        double p95 = latencies[(int)(latencies.Count * 0.95)];
        double p99 = latencies[(int)(latencies.Count * 0.99)];

        _output.WriteLine($"=== UI Responsiveness Benchmark Telemetry ({latencies.Count} samples) ===");
        _output.WriteLine($"  Min:  {min:F3} ms");
        _output.WriteLine($"  Mean: {mean:F3} ms");
        _output.WriteLine($"  P50:  {p50:F3} ms");
        _output.WriteLine($"  P95:  {p95:F3} ms");
        _output.WriteLine($"  P99:  {p99:F3} ms");
        _output.WriteLine($"  Max:  {max:F3} ms");

        // 5. Assert strict sub-100ms invariant
        Assert.True(mean < 15.0, $"Expected Mean latency < 15ms, but was {mean:F2}ms");
        Assert.True(p95 < 40.0, $"Expected P95 latency < 40ms, but was {p95:F2}ms");
        Assert.True(p99 < 75.0, $"Expected P99 latency < 75ms, but was {p99:F2}ms");
        Assert.True(max < 100.0, $"Expected Max UI latency < 100ms, but was {max:F2}ms");
    }

    private class MockStreamingAndToolExecutionHttpHandler : HttpMessageHandler
    {
        private readonly CancellationToken _externalCt;
        private int _requestCount = 0;

        public MockStreamingAndToolExecutionHttpHandler(CancellationToken externalCt)
        {
            _externalCt = externalCt;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _externalCt);
            int count = Interlocked.Increment(ref _requestCount);

            Stream stream = count == 1
                ? new ToolCallingThenInfiniteSseStream(linkedCts.Token)
                : new InfiniteTokenSseStream(linkedCts.Token);

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(resp);
        }
    }

    private class ToolCallingThenInfiniteSseStream : Stream
    {
        private readonly CancellationToken _ct;
        private int _tokenIndex = 0;
        private bool _emittedTool = false;
        private bool _done = false;

        public ToolCallingThenInfiniteSseStream(CancellationToken ct) => _ct = ct;

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
            if (_done) return 0;

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ct);
            // Emulate 100 tok/s (1 token every 10ms)
            await Task.Delay(10, linked.Token);

            string sseChunk;
            if (_tokenIndex < 15)
            {
                sseChunk = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\" tok_{_tokenIndex++}\"}}}}]}}\n\n";
            }
            else if (!_emittedTool)
            {
                _emittedTool = true;
                sseChunk = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_resp_1\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo ui_bench_tool_running\\\"}\"}}]}}]}\n\n";
            }
            else
            {
                _done = true;
                sseChunk = "data: [DONE]\n\n";
            }

            byte[] bytes = Encoding.UTF8.GetBytes(sseChunk);
            int toCopy = Math.Min(bytes.Length, buffer.Length);
            bytes.AsMemory(0, toCopy).CopyTo(buffer);
            return toCopy;
        }
    }

    private class InfiniteTokenSseStream : Stream
    {
        private readonly CancellationToken _ct;
        private int _tokenIndex = 100;

        public InfiniteTokenSseStream(CancellationToken ct) => _ct = ct;

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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _ct);
            // Emulate 100 tok/s (1 token every 10ms)
            await Task.Delay(10, linked.Token);

            string sseChunk = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\" tok_{_tokenIndex++}\"}}}}]}}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sseChunk);
            int toCopy = Math.Min(bytes.Length, buffer.Length);
            bytes.AsMemory(0, toCopy).CopyTo(buffer);
            return toCopy;
        }
    }
}
