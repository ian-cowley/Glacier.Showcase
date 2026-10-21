using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

[Collection("GlacierStudioSessionTests")]
public class GlacierStudioStreamingAdversarialTests
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

    private class CancellableHangingStream : Stream
    {
        private readonly byte[] _initialBytes;
        private int _initialReadOffset = 0;

        public CancellableHangingStream(string initialContent)
        {
            _initialBytes = Encoding.UTF8.GetBytes(initialContent);
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
            if (_initialReadOffset < _initialBytes.Length)
            {
                int remaining = _initialBytes.Length - _initialReadOffset;
                int toCopy = Math.Min(remaining, buffer.Length);
                _initialBytes.AsMemory(_initialReadOffset, toCopy).CopyTo(buffer);
                _initialReadOffset += toCopy;
                return toCopy;
            }

            // Hang indefinitely until cancelled via cancellationToken
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    private static HttpResponseMessage CreateSseResponse(string ssePayload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream");
        return response;
    }

    private static (GlacierAgentEngine engine, GlacierStudioState state, GlacierConsoleService console) CreateTestHarness(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> httpHandler)
    {
        var handler = new MockHttpHandler(httpHandler);
        var httpClient = new HttpClient(handler);
        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreviewService = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreviewService,
            mcpManager);

        return (engine, state, consoleService);
    }

    // =========================================================================
    // 1. Stress Test: StreamingToolCallAccumulator Fragmented Argument Chunks
    // =========================================================================

    [Fact]
    public void StreamingToolCallAccumulator_DirectAccumulation_StressFragments()
    {
        var acc = new StreamingToolCallAccumulator { Index = 0 };
        acc.Id = "call_test_fragment";
        acc.Name = "run_command";

        // Fragment JSON string across 16 tiny chunks
        var fragments = new[]
        {
            "{\"", "co", "mm", "an", "d\":", " \"g", "it", " ", "st", "at", "us", "\",",
            " \"fl", "ag\":", " true", "}"
        };

        foreach (var frag in fragments)
        {
            acc.ArgumentsBuilder.Append(frag);
        }

        string fullArgs = acc.ArgumentsBuilder.ToString();
        Assert.Equal("{\"command\": \"git status\", \"flag\": true}", fullArgs);

        var sanitized = ToolArgumentSanitizer.ParseAndSanitize(fullArgs);
        Assert.True(sanitized.TryGetProperty("command", out var cmdProp));
        Assert.Equal("git status", cmdProp.GetString());
        Assert.True(sanitized.TryGetProperty("flag", out var flagProp));
        Assert.True(flagProp.GetBoolean());
    }

    [Fact]
    public async Task StreamingModel_EndToEnd_AggregatesFragmentedToolCallDeltasAcrossMultipleEvents()
    {
        int requestCount = 0;

        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            requestCount++;

            if (requestCount == 1)
            {
                // Turn 1: Stream tool call in multiple fragmented SSE events
                var sb = new StringBuilder();
                // Event 1: Tool call init with id and name fragment
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_frag_123\",\"type\":\"function\",\"function\":{\"name\":\"run_\"}}]}}]}");
                sb.AppendLine();
                // Event 2: Name completion
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"command\"}}]}}]}");
                sb.AppendLine();
                // Event 3-9: Piecewise JSON argument fragments
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"{\\\"\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"co\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"mmand\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\": \\\"\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"echo FragmentedPass\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"}\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
            else
            {
                // Turn 2: Assistant returns final message
                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Command finished successfully.\"}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
        });

        await engine.ProcessUserMessageAsync("Please run the fragmented command.");

        var session = state.ActiveSession;
        Assert.True(session.Messages.Count >= 3, $"Expected at least 3 messages, got {session.Messages.Count}");

        // Find assistant message with tool call
        var assistantMsg = session.Messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        Assert.NotNull(assistantMsg);
        Assert.Single(assistantMsg.ToolCalls);

        var toolCall = assistantMsg.ToolCalls[0];
        Assert.Equal("call_frag_123", toolCall.Id);
        Assert.Equal("run_command", toolCall.ToolName);
        Assert.Equal("{\"command\": \"echo FragmentedPass\"}", toolCall.ArgumentsJson);

        // Find tool message
        var toolMsg = session.Messages.FirstOrDefault(m => m.Role == "tool");
        Assert.NotNull(toolMsg);
        Assert.Equal("call_frag_123", toolMsg.ToolCallId);
        Assert.Equal("run_command", toolMsg.ToolName);
        Assert.Contains("FragmentedPass", toolMsg.Content);

        // Verify final state
        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);
    }

    // =========================================================================
    // 2. Stress Test: Partial JSON Chunks & Corrupted Lines During Streaming (No Crashes)
    // =========================================================================

    [Fact]
    public async Task StreamingModel_StressTest_PartialAndCorruptedSseLinesDoNotCrash()
    {
        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            var sb = new StringBuilder();

            // Comment lines / ping keepalives
            sb.AppendLine(": ping keepalive 1");
            sb.AppendLine(":");

            // Valid content chunk 1
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Part 1 \"}}]}");
            sb.AppendLine();

            // Corrupted line: truncated JSON
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Truncated line that cuts off");

            // Corrupted line: plain text error message from proxy
            sb.AppendLine("data: 502 Bad Gateway - Server Temporary Error");

            // Empty data line
            sb.AppendLine("data: ");
            sb.AppendLine("data:");

            // Valid content chunk 2
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Part 2\"}}]}");
            sb.AppendLine();

            // Usage stats
            sb.AppendLine("data: {\"usage\":{\"completion_tokens\":10,\"prompt_tokens\":5,\"total_tokens\":15}}");
            sb.AppendLine();

            sb.AppendLine("data: [DONE]");
            sb.AppendLine();

            return CreateSseResponse(sb.ToString());
        });

        // Must NOT throw JsonException or JsonReaderException
        await engine.ProcessUserMessageAsync("Test resilient streaming");

        var session = state.ActiveSession;
        var assistantMsg = session.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(assistantMsg);
        Assert.Contains("Part 1 Part 2", assistantMsg.Content);
        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);
        Assert.False(assistantMsg.IsError);
    }

    [Fact]
    public void ToolArgumentSanitizer_AdversarialStressFuzzing_NeverThrows()
    {
        // Suite of adversarial inputs designed to break naive JSON parsers
        var adversarialInputs = new[]
        {
            "",
            "   ",
            null,
            "```json\n{\"command\": \"dir\"}\n```",
            "```\n{\"command\": \"dir\"}\n```",
            "{\"command\": \"echo hello", // Truncated JSON
            "not json at all",
            "C:\\Users\\spuri\\source\\repos\\test.txt", // Bare Windows path
            "{\"path\": \"C:\\Users\\spuri\\source\\repos\\test.txt\"}", // Unescaped backslashes in Windows path
            "{\"path\": \"C:\\\\Users\\\\spuri\\Documents\\file.txt\"}", // Mixed escaped and unescaped
            "{\"command\": \"echo \\\"quoted\\\"\"}", // Nested quotes
            "[1, 2, 3]", // Array instead of object
            "12345", // Bare number
            "\"just a string\"", // Bare quoted string
            "{\"raw\": {\"nested\": true}}",
            "```json\n{\"path\": \"D:\\Data\\Models\\qwen.gguf\", \"verbose\": true}\n```"
        };

        foreach (var input in adversarialInputs)
        {
            var element = ToolArgumentSanitizer.ParseAndSanitize(input!);
            Assert.Equal(JsonValueKind.Object, element.ValueKind);
        }
    }

    [Fact]
    public void ToolArgumentSanitizer_WindowsPaths_RepairsBackslashesCorrectly()
    {
        string raw = "{\"path\": \"C:\\Users\\test\\folder\\sub\\file.txt\"}";
        var element = ToolArgumentSanitizer.ParseAndSanitize(raw);

        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        Assert.True(element.TryGetProperty("path", out var pProp));
        Assert.Equal("C:\\Users\\test\\folder\\sub\\file.txt", pProp.GetString());
    }

    // =========================================================================
    // 3. Stress Test: Multiple Simultaneous & Interleaved Tool Calls
    // =========================================================================

    [Fact]
    public async Task StreamingModel_EndToEnd_HandlesMultipleSimultaneousInterleavedToolCalls()
    {
        int requestCount = 0;

        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            requestCount++;

            if (requestCount == 1)
            {
                // Turn 1: Stream 2 simultaneous tool calls with interleaved deltas
                var sb = new StringBuilder();

                // Delta 1: Index 0 starts (echo call)
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_echo_0\",\"type\":\"function\",\"function\":{\"name\":\"run_\"}}]}}]}");
                sb.AppendLine();

                // Delta 2: Index 1 starts (read_file call)
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"call_read_1\",\"type\":\"function\",\"function\":{\"name\":\"read_\"}}]}}]}");
                sb.AppendLine();

                // Delta 3: Index 0 name completion + args fragment
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"command\",\"arguments\":\"{\\\"command\\\": \\\"\"}}]}}]}");
                sb.AppendLine();

                // Delta 4: Index 1 name completion + args fragment
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"name\":\"file\",\"arguments\":\"{\\\"path\\\": \\\"\"}}]}}]}");
                sb.AppendLine();

                // Delta 5: Index 0 args continuation
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"echo InterleavedEcho\"}}]}}]}");
                sb.AppendLine();

                // Delta 6: Index 1 args continuation
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"README.md\"}}]}}]}");
                sb.AppendLine();

                // Delta 7: Index 0 args closure
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"}\"}}]}}]}");
                sb.AppendLine();

                // Delta 8: Index 1 args closure
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"function\":{\"arguments\":\"\\\"}\"}}]}}]}");
                sb.AppendLine();

                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
            else
            {
                // Turn 2: Assistant final response
                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Both interleaved tools executed successfully.\"}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
        });

        await engine.ProcessUserMessageAsync("Run two interleaved tools");

        var session = state.ActiveSession;
        var assistantMsg = session.Messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls.Count > 0);

        Assert.NotNull(assistantMsg);
        Assert.Equal(2, assistantMsg.ToolCalls.Count);

        // Verify Tool Call 0
        var tc0 = assistantMsg.ToolCalls[0];
        Assert.Equal("call_echo_0", tc0.Id);
        Assert.Equal("run_command", tc0.ToolName);
        Assert.Equal("{\"command\": \"echo InterleavedEcho\"}", tc0.ArgumentsJson);
        Assert.True(tc0.IsSuccess);
        Assert.Contains("InterleavedEcho", tc0.ResultOutput);

        // Verify Tool Call 1
        var tc1 = assistantMsg.ToolCalls[1];
        Assert.Equal("call_read_1", tc1.Id);
        Assert.Equal("read_file", tc1.ToolName);
        Assert.Equal("{\"path\": \"README.md\"}", tc1.ArgumentsJson);

        // Verify Tool Messages in session history
        var toolMsgs = session.Messages.Where(m => m.Role == "tool").ToList();
        Assert.Equal(2, toolMsgs.Count);
        Assert.Equal("call_echo_0", toolMsgs[0].ToolCallId);
        Assert.Equal("call_read_1", toolMsgs[1].ToolCallId);

        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);
    }

    [Fact]
    public async Task StreamingModel_EndToEnd_HandlesMultipleToolCallsInSingleDeltaArray()
    {
        int requestCount = 0;

        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            requestCount++;

            if (requestCount == 1)
            {
                // Turn 1: Single SSE delta event containing 2 tool calls in the array
                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[" +
                    "{\"index\":0,\"id\":\"call_batch_0\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo BatchZero\\\"}\"}}," +
                    "{\"index\":1,\"id\":\"call_batch_1\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo BatchOne\\\"}\"}}" +
                    "]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Batch complete\"}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
        });

        await engine.ProcessUserMessageAsync("Run batch tool calls");

        var session = state.ActiveSession;
        var assistantMsg = session.Messages.FirstOrDefault(m => m.Role == "assistant" && m.ToolCalls.Count > 0);

        Assert.NotNull(assistantMsg);
        Assert.Equal(2, assistantMsg.ToolCalls.Count);
        Assert.Equal("call_batch_0", assistantMsg.ToolCalls[0].Id);
        Assert.Equal("call_batch_1", assistantMsg.ToolCalls[1].Id);
    }

    // =========================================================================
    // 4. Stress Test: Cancellation Token Support & Abrupt Abort
    // =========================================================================

    [Fact]
    public async Task StreamingModel_Cancellation_AbortsStreamCleanlyWithoutHanging()
    {
        var hangingStreamResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancellableHangingStream(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Initial streaming text... \"}}]}\n\n"))
        };
        hangingStreamResponse.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");

        var (engine, state, _) = CreateTestHarness((req, ct) => Task.FromResult(hangingStreamResponse));

        // Start execution in background task
        var processTask = engine.ProcessUserMessageAsync("This stream will hang until cancelled");

        // Give it a brief moment to begin streaming
        await Task.Delay(100);

        // Cancel execution
        engine.CancelCurrentExecution();

        // Must complete promptly (< 2000 ms) and not deadlock
        var completedTask = await Task.WhenAny(processTask, Task.Delay(3000));
        Assert.Same(processTask, completedTask);

        // Verify state is properly marked Cancelled
        Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
        Assert.True(state.StatusText == "Interrupted by user" || state.StatusText == "Cancelled by user",
            $"Expected StatusText to be 'Interrupted by user' or 'Cancelled by user', got '{state.StatusText}'");

        var session = state.ActiveSession;
        var lastMsg = session.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(lastMsg);
        Assert.Contains("*(Execution was cancelled)*", lastMsg.Content);
    }

    // =========================================================================
    // 5. Stress Test: ReAct Message Sequence Integrity (Assistant tool_calls)
    // =========================================================================

    [Fact]
    public async Task ReActMessageSequence_AssistantMessageIncludesToolCallsBeforeToolResponse()
    {
        string? turn2CapturedJson = null;
        int requestCount = 0;

        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            requestCount++;

            if (requestCount == 1)
            {
                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_seq_test\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"echo SeqOk\\\"}\"}}]}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
            else
            {
                // Capture the Turn 2 payload
                turn2CapturedJson = await req.Content!.ReadAsStringAsync(ct);

                var sb = new StringBuilder();
                sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"Final ReAct response\"}}]}");
                sb.AppendLine();
                sb.AppendLine("data: [DONE]");
                sb.AppendLine();

                return CreateSseResponse(sb.ToString());
            }
        });

        await engine.ProcessUserMessageAsync("Test ReAct message sequence");

        Assert.NotNull(turn2CapturedJson);

        using var doc = JsonDocument.Parse(turn2CapturedJson!);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("messages", out var messagesArray));
        Assert.Equal(JsonValueKind.Array, messagesArray.ValueKind);

        // Find the assistant message in the Turn 2 request payload
        JsonElement? turn2Assistant = null;
        JsonElement? turn2Tool = null;

        foreach (var msg in messagesArray.EnumerateArray())
        {
            string role = msg.GetProperty("role").GetString() ?? "";
            if (role == "assistant" && msg.TryGetProperty("tool_calls", out _))
            {
                turn2Assistant = msg;
            }
            else if (role == "tool")
            {
                turn2Tool = msg;
            }
        }

        // Must serialize tool_calls onto the assistant message!
        Assert.NotNull(turn2Assistant);
        var toolCallsProp = turn2Assistant.Value.GetProperty("tool_calls");
        Assert.Equal(JsonValueKind.Array, toolCallsProp.ValueKind);
        Assert.True(toolCallsProp.GetArrayLength() > 0);

        var firstCall = toolCallsProp[0];
        Assert.Equal("call_seq_test", firstCall.GetProperty("id").GetString());
        Assert.Equal("function", firstCall.GetProperty("type").GetString());
        Assert.Equal("run_command", firstCall.GetProperty("function").GetProperty("name").GetString());

        // And subsequent message must be role: tool with matching tool_call_id
        Assert.NotNull(turn2Tool);
        Assert.Equal("call_seq_test", turn2Tool.Value.GetProperty("tool_call_id").GetString());
        Assert.Contains("SeqOk", turn2Tool.Value.GetProperty("content").GetString());
    }

    // =========================================================================
    // 6. Stress Test: Reasoning Content & Inline <think> Tags
    // =========================================================================

    [Fact]
    public async Task StreamingModel_HandlesReasoningDeltasAndInlineThinkTags()
    {
        var (engine, state, _) = CreateTestHarness(async (req, ct) =>
        {
            var sb = new StringBuilder();

            // Reasoning delta (DeepSeek-R1 style)
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Deep reasoning step 1. \"}}]}");
            sb.AppendLine();
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Deep reasoning step 2.\"}}]}");
            sb.AppendLine();

            // Content with inline <think> tags (Qwen / Ollama style)
            sb.AppendLine("data: {\"choices\":[{\"delta\":{\"content\":\"<think>Inline thought</think>Answer text.\"}}]}");
            sb.AppendLine();

            sb.AppendLine("data: [DONE]");
            sb.AppendLine();

            return CreateSseResponse(sb.ToString());
        });

        await engine.ProcessUserMessageAsync("Solve reasoning problem");

        var session = state.ActiveSession;
        var assistantMsg = session.Messages.LastOrDefault(m => m.Role == "assistant");

        Assert.NotNull(assistantMsg);
        Assert.Contains("Answer text.", assistantMsg.Content);
        Assert.DoesNotContain("<think>", assistantMsg.Content);
        Assert.NotNull(assistantMsg.ReasoningContent);
        Assert.Contains("Deep reasoning step 1.", assistantMsg.ReasoningContent);
        Assert.Contains("Inline thought", assistantMsg.ReasoningContent);
    }
}
