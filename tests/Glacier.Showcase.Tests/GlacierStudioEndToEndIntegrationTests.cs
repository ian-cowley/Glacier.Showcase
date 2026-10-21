using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Inference.Gpu;
using Glacier.Inference.Hardware;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Comprehensive End-to-End Integration Tests for Glacier Agent Studio (Antigravity 2).
/// Verifies full autonomous agent cycles, piecewise streaming tool calls, JSON-RPC stdio MCP execution,
/// ReAct sequence compliance, session disk persistence, search, export, ANSI console workflows,
/// DocTree line navigation, and physical hardware telemetry.
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierStudioEndToEndIntegrationTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;
    private readonly string _exportsDir;
    private readonly string _tempTestDir;
    private readonly List<GlacierMcpManager> _spawnedMcpManagers = new();

    public GlacierStudioEndToEndIntegrationTests()
    {
        _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        _sessionsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "sessions");
        _exportsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "exports");
        _tempTestDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "e2e_test_scratch_" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_exportsDir);
        Directory.CreateDirectory(_tempTestDir);
    }

    public void Dispose()
    {
        // Clean up spawned MCP managers
        foreach (var mcp in _spawnedMcpManagers)
        {
            try { mcp.Dispose(); } catch { }
        }

        // Clean up test sessions created during test run
        if (Directory.Exists(_sessionsDir))
        {
            var testMarkers = new[] { "unit_test_", "adv_test_", "e2e_test_", "challenger_sec_", "test_" };
            foreach (var file in Directory.GetFiles(_sessionsDir, "*.json"))
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    if (testMarkers.Any(m => fileName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        File.Delete(file);
                        continue;
                    }

                    string content = File.ReadAllText(file);
                    if (testMarkers.Any(m => content.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    {
                        File.Delete(file);
                    }
                }
                catch { }
            }
        }

        // Clean up test exports
        if (Directory.Exists(_exportsDir))
        {
            foreach (var file in Directory.GetFiles(_exportsDir, "*e2e_test_*"))
            {
                try { File.Delete(file); } catch { }
            }
        }

        // Clean up temporary scratch directory
        if (Directory.Exists(_tempTestDir))
        {
            try { Directory.Delete(_tempTestDir, recursive: true); } catch { }
        }
    }

    #region Mock HTTP Infrastructure

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

    private static HttpResponseMessage CreateSseResponse(string ssePayload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Content = new StringContent(ssePayload, Encoding.UTF8, "text/event-stream");
        return response;
    }

    private (GlacierAgentEngine engine, GlacierStudioState state, GlacierConsoleService console, GlacierDocTreePreviewService docTree, GlacierMcpManager mcp)
        CreateTestHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> httpHandler)
    {
        var handler = new MockHttpHandler(httpHandler);
        var httpClient = new HttpClient(handler);
        var state = new GlacierStudioState { IsAgentMode = true };
        var consoleService = new GlacierConsoleService();
        var docPreviewService = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();
        _spawnedMcpManagers.Add(mcpManager);

        var engine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreviewService,
            mcpManager);

        return (engine, state, consoleService, docPreviewService, mcpManager);
    }

    #endregion

    // =========================================================================
    // 1. Full Autonomous Agent Cycle: Streaming -> Piecewise Tool Call -> Stdio MCP -> Turn 2 -> Auto-Save -> Export
    // =========================================================================

    [Fact]
    public async Task AutonomousAgentCycle_FullReActLoop_WithPiecewiseStreamingAndMcpExecution()
    {
        int requestTurn = 0;
        var capturedTurn2Payload = new StringBuilder();

        var (engine, state, consoleService, docTree, mcp) = CreateTestHarness(async (req, ct) =>
        {
            requestTurn++;
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.True(req.Headers.Accept.Any(a => a.MediaType == "text/event-stream"), "Accept header must request text/event-stream.");

            string body = await req.Content!.ReadAsStringAsync(ct);

            if (requestTurn == 1)
            {
                // Turn 1: Model emits text, thinking reasoning, and piecewise delta tool call invoking call_mcp_tool
                string sseTurn1 =
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Initiating GlacierGraphEngine diagnostics.\\n\"}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"Executing ping on stdio MCP server to verify JSON-RPC health.\\n\"}}]}\n\n" +
                    // Piecewise delta tool call: chunk 1 with server name
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_mcp_e2e_999\",\"type\":\"function\",\"function\":{\"name\":\"call_mcp_tool\",\"arguments\":\"{\\\"server\\\":\\\"GlacierGraphEngine\\\",\\\"tool\\\":\\\"ping\\\",\"}}]}}]}\n\n" +
                    // Piecewise delta tool call: chunk 2 completing arguments
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"arguments\\\":\\\"{}\\\"}\"}}]}}]}\n\n" +
                    // Finish turn 1
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"completion_tokens\":35,\"prompt_tokens\":120,\"total_tokens\":155}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn1);
            }
            else if (requestTurn == 2)
            {
                // Turn 2: Capture payload to verify assistant tool_calls sequence preceding role: tool
                capturedTurn2Payload.Append(body);

                string sseTurn2 =
                    "data: {\"id\":\"chatcmpl-turn2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"GlacierGraphEngine stdio ping succeeded. JSON-RPC 2.0 communication is active and verified.\\n\"}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-turn2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":20,\"prompt_tokens\":185,\"total_tokens\":205}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn2);
            }

            throw new InvalidOperationException($"Unexpected request turn {requestTurn}");
        });

        // Initialize active session with distinct test title
        string sessionTitle = $"e2e_test_AgentCycle_{Guid.NewGuid():N}[..8]";
        var session = state.CreateNewSession(sessionTitle);
        Assert.Equal(session.Id, state.ActiveSession.Id);

        // Run full autonomous agent cycle
        await engine.ProcessUserMessageAsync("Run health check on GlacierGraphEngine via MCP ping.");

        // Assert 2 model requests were made (Turn 1 tool dispatch + Turn 2 final answer)
        Assert.Equal(2, requestTurn);

        // Verify ReAct message sequence in Turn 2 payload
        string turn2Json = capturedTurn2Payload.ToString();
        Assert.NotEmpty(turn2Json);
        using (var turn2Doc = JsonDocument.Parse(turn2Json))
        {
            var messages = turn2Doc.RootElement.GetProperty("messages");
            Assert.True(messages.GetArrayLength() >= 4);

            // Locate assistant message preceding role: tool
            bool foundAssistantWithToolCalls = false;
            bool foundToolMessageWithMatchingId = false;

            for (int i = 0; i < messages.GetArrayLength(); i++)
            {
                var msg = messages[i];
                string role = msg.GetProperty("role").GetString()!;

                if (role == "assistant" && msg.TryGetProperty("tool_calls", out var toolCallsElem))
                {
                    Assert.Equal(JsonValueKind.Array, toolCallsElem.ValueKind);
                    var firstCall = toolCallsElem[0];
                    Assert.Equal("call_mcp_e2e_999", firstCall.GetProperty("id").GetString());
                    Assert.Equal("call_mcp_tool", firstCall.GetProperty("function").GetProperty("name").GetString());
                    foundAssistantWithToolCalls = true;

                    // Immediately following message must be role: tool with matching tool_call_id
                    if (i + 1 < messages.GetArrayLength())
                    {
                        var nextMsg = messages[i + 1];
                        Assert.Equal("tool", nextMsg.GetProperty("role").GetString());
                        Assert.Equal("call_mcp_e2e_999", nextMsg.GetProperty("tool_call_id").GetString());
                        foundToolMessageWithMatchingId = true;
                    }
                }
            }

            Assert.True(foundAssistantWithToolCalls, "Turn 2 payload must serialize tool_calls array onto assistant message.");
            Assert.True(foundToolMessageWithMatchingId, "Turn 2 payload must serialize role: tool immediately following assistant tool_calls.");
        }

        // Verify session state transitions and completion metrics
        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);
        Assert.True(state.LastTokensPerSecond > 0, "Tokens per second must be calculated from genuine token telemetry.");
        Assert.True(state.ActiveContextTokens > 0, "Active context tokens must reflect usage metrics.");
        Assert.True(state.LastTurnDuration > TimeSpan.Zero, "Turn duration must be greater than zero.");

        // Verify messages in active session
        // Messages: 1: user, 2: assistant (with tool call & reasoning), 3: tool, 4: assistant (final answer)
        Assert.True(session.Messages.Count >= 4);
        var userMsg = session.Messages.First(m => m.Role == "user");
        Assert.Equal("Run health check on GlacierGraphEngine via MCP ping.", userMsg.Content);

        var toolAssistantMsg = session.Messages.First(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        Assert.Single(toolAssistantMsg.ToolCalls);
        var toolRecord = toolAssistantMsg.ToolCalls[0];
        Assert.Equal("call_mcp_e2e_999", toolRecord.Id);
        Assert.Equal("call_mcp_tool", toolRecord.ToolName);
        Assert.NotNull(toolRecord.ResultOutput);
        Assert.NotEmpty(toolRecord.ResultOutput);
        Assert.True(toolRecord.Duration >= TimeSpan.Zero);

        var finalAssistantMsg = session.Messages.Last(m => m.Role == "assistant");
        Assert.Contains("GlacierGraphEngine stdio ping succeeded", finalAssistantMsg.Content);

        // Auto-save session to disk
        state.SaveActiveSession();
        string expectedSessionFile = Path.Combine(_sessionsDir, $"{session.Id}.json");
        Assert.True(File.Exists(expectedSessionFile), $"Session file must exist at {expectedSessionFile}");

        // Export active session to Markdown
        string markdownExport = state.ExportActiveSessionAsMarkdown();
        Assert.Contains($"# {session.Title}", markdownExport);
        Assert.Contains("call_mcp_tool", markdownExport);
        Assert.Contains("GlacierGraphEngine stdio ping succeeded", markdownExport);

        // Export active session to JSON
        string jsonExport = state.ExportActiveSessionAsJson();
        var reloaded = JsonSerializer.Deserialize<StudioSession>(jsonExport);
        Assert.NotNull(reloaded);
        Assert.Equal(session.Id, reloaded.Id);
        Assert.Equal(session.Messages.Count, reloaded.Messages.Count);

        // Export session to disk as both MD and JSON
        string diskMdPath = state.ExportActiveSessionToDisk("md");
        string diskJsonPath = state.ExportActiveSessionToDisk("json");
        Assert.True(File.Exists(diskMdPath), "Exported markdown file must exist on disk.");
        Assert.True(File.Exists(diskJsonPath), "Exported json file must exist on disk.");

        // Verify contents of exported files
        string diskMdContent = await File.ReadAllTextAsync(diskMdPath);
        Assert.Contains(sessionTitle, diskMdContent);
        string diskJsonContent = await File.ReadAllTextAsync(diskJsonPath);
        Assert.Contains(session.Id, diskJsonContent);

        // Clean up test session from disk
        state.DeleteSession(session.Id);
    }

    // =========================================================================
    // 2. Full Autonomous Agent Cycle: Terminal Execution & Directory Tracking
    // =========================================================================

    [Fact]
    public async Task AutonomousAgentCycle_TerminalExecution_WithPowerShellAndDirectoryTracking()
    {
        int requestTurn = 0;

        var (engine, state, consoleService, docTree, mcp) = CreateTestHarness(async (req, ct) =>
        {
            requestTurn++;
            string body = await req.Content!.ReadAsStringAsync(ct);

            if (requestTurn == 1)
            {
                // Turn 1 calls run_command
                string sseTurn1 =
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Executing directory check in console.\\n\"}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_cmd_001\",\"type\":\"function\",\"function\":{\"name\":\"run_command\",\"arguments\":\"{\\\"command\\\":\\\"Write-Output 'Glacier-Terminal-Active'\\\"}\"}}]}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-turn1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"completion_tokens\":25,\"prompt_tokens\":90,\"total_tokens\":115}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn1);
            }
            else if (requestTurn == 2)
            {
                // Turn 2 final answer
                string sseTurn2 =
                    "data: {\"id\":\"chatcmpl-turn2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Command completed successfully: terminal is active.\\n\"}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-turn2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":15,\"prompt_tokens\":140,\"total_tokens\":155}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn2);
            }

            throw new InvalidOperationException($"Unexpected request turn {requestTurn}");
        });

        string testTitle = $"e2e_test_Terminal_{Guid.NewGuid():N}[..8]";
        var session = state.CreateNewSession(testTitle);

        await engine.ProcessUserMessageAsync("Test terminal command execution.");

        Assert.Equal(2, requestTurn);
        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);

        // Verify console logs contain the executed command
        Assert.NotEmpty(consoleService.Logs);
        Assert.Contains(consoleService.Logs, l => l.Message.Contains("Glacier-Terminal-Active") || l.Message.Contains("Write-Output"));

        // Verify tool record captured exit code 0
        var assistantMsg = session.Messages.First(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        var toolRecord = assistantMsg.ToolCalls[0];
        Assert.Equal("run_command", toolRecord.ToolName);
        Assert.Contains("Exit Code: 0", toolRecord.ResultOutput);
        Assert.Contains("Glacier-Terminal-Active", toolRecord.ResultOutput);

        // Auto-save and verify persistence
        state.SaveActiveSession();
        string sessionFile = Path.Combine(_sessionsDir, $"{session.Id}.json");
        Assert.True(File.Exists(sessionFile));

        // Clean up test session from disk
        state.DeleteSession(session.Id);
    }

    // =========================================================================
    // 3. Multi-Turn Conversation Persistence, Reload & Search Query
    // =========================================================================

    [Fact]
    public void MultiTurnConversationPersistence_SaveLoadSearchAndVerify()
    {
        var state1 = new GlacierStudioState();
        string uniqueTag = Guid.NewGuid().ToString("N")[..8];
        string title = $"e2e_test_PersistenceSession_{uniqueTag}";

        var session = state1.CreateNewSession(title);
        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = $"What is the vector dimension supported in Glacier.Vector? Tag:{uniqueTag}",
            Timestamp = DateTime.Now
        });
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = $"Glacier.Vector natively supports dense 1536-dim and 512-dim embedding spaces with AVX-512 SIMD kernels. Tag:{uniqueTag}",
            ReasoningContent = "Reviewing Glacier.Vector distance kernels and memory layout.",
            Timestamp = DateTime.Now
        });
        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Are dot product and cosine similarity zero-allocation?",
            Timestamp = DateTime.Now
        });
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Yes, both operations operate directly on unmanaged ReadOnlySpan<float> with 0 heap allocations.",
            Timestamp = DateTime.Now
        });

        // Save active session to disk and ensure persistence completed
        string expectedPath = Path.Combine(_sessionsDir, $"{session.Id}.json");
        bool persistedWithMessages = false;
        for (int retry = 0; retry < 10; retry++)
        {
            state1.SaveActiveSession();
            if (File.Exists(expectedPath))
            {
                try
                {
                    string json = File.ReadAllText(expectedPath);
                    var checkSession = JsonSerializer.Deserialize<StudioSession>(json);
                    if (checkSession != null && checkSession.Messages.Count == session.Messages.Count)
                    {
                        persistedWithMessages = true;
                        break;
                    }
                }
                catch { }
            }
            Thread.Sleep(50);
        }
        Assert.True(persistedWithMessages, $"Session file with {session.Messages.Count} messages should exist on disk at {expectedPath}");

        // Instantiate brand new fresh GlacierStudioState
        var state2 = new GlacierStudioState();

        // Verify the persisted session was loaded from disk into state2
        var loadedSession = state2.Sessions.FirstOrDefault(s => s.Id == session.Id);
        if (loadedSession == null || loadedSession.Messages.Count != session.Messages.Count)
        {
            state2.LoadSessionsFromDisk();
            loadedSession = state2.Sessions.FirstOrDefault(s => s.Id == session.Id);
        }
        Assert.NotNull(loadedSession);
        Assert.Equal(title, loadedSession.Title);
        Assert.Equal(session.Messages.Count, loadedSession.Messages.Count);
        Assert.Equal(session.Messages[0].Content, loadedSession.Messages[0].Content);
        Assert.Equal(session.Messages[1].Content, loadedSession.Messages[1].Content);
        Assert.Equal("Reviewing Glacier.Vector distance kernels and memory layout.", loadedSession.Messages[1].ReasoningContent);

        // Test search query by title tag
        state2.SessionSearchQuery = uniqueTag;
        var filteredByTitle = state2.FilteredSessions.ToList();
        Assert.Contains(filteredByTitle, s => s.Id == session.Id);

        // Test search query by message content keyword
        state2.SessionSearchQuery = "AVX-512 SIMD";
        var filteredByContent = state2.FilteredSessions.ToList();
        Assert.Contains(filteredByContent, s => s.Id == session.Id);

        // Test search query with non-matching string
        state2.SessionSearchQuery = "completely_nonexistent_token_99999";
        var filteredEmpty = state2.FilteredSessions.ToList();
        Assert.DoesNotContain(filteredEmpty, s => s.Id == session.Id);

        // Reset search query
        state2.SessionSearchQuery = "";
        Assert.Contains(state2.FilteredSessions, s => s.Id == session.Id);

        // Switch to session and verify active
        state2.SwitchSession(session.Id);
        Assert.Equal(session.Id, state2.ActiveSession.Id);

        // Delete session and verify removal from memory and disk
        state2.DeleteSession(session.Id);
        Assert.DoesNotContain(state2.Sessions, s => s.Id == session.Id);
        Assert.False(File.Exists(expectedPath), "Deleted session file must be purged from disk.");
    }

    // =========================================================================
    // 4. ANSI Console Workflow: Working Directory Persistence & Color Parsing
    // =========================================================================

    [Fact]
    public async Task AnsiConsoleWorkflow_WorkingDirectoryPersistenceAndColorFormatting()
    {
        var service = new GlacierConsoleService();
        string initialCwd = service.CurrentWorkingDirectory;
        Assert.True(Directory.Exists(initialCwd), "Initial working directory must exist on disk.");

        // 1. Change directory into parent via cd ..
        var (cdParentCode, cdParentOut) = await service.ExecuteCommandAsync("cd ..");
        Assert.Equal(0, cdParentCode);
        string expectedParentPath = Path.GetFullPath(Path.Combine(initialCwd, ".."));
        Assert.Equal(expectedParentPath, service.CurrentWorkingDirectory);

        // 2. Change directory back into initial directory
        string initialDirName = Path.GetFileName(initialCwd);
        var (cdBackCode, cdBackOut) = await service.ExecuteCommandAsync($"cd \"{initialDirName}\"");
        Assert.Equal(0, cdBackCode);
        Assert.Equal(initialCwd, service.CurrentWorkingDirectory);

        // 3. Execute command and verify child process reflects current directory
        var (dirCode, dirOut) = await service.ExecuteCommandAsync("[System.IO.Directory]::GetCurrentDirectory()");
        Assert.Equal(0, dirCode);
        Assert.Contains(initialCwd, dirOut);

        // 4. Test ANSI escape sequence parsing with multiple color palettes and styles
        string ansiGreen = "\u001b[32mPASS: All 21 Features Verified\u001b[0m";
        string htmlGreen = GlacierAnsiParser.ToHtml(ansiGreen);
        Assert.Contains("color:#22c55e", htmlGreen);
        Assert.Contains("PASS: All 21 Features Verified", htmlGreen);

        string ansiRedBold = "\u001b[1m\u001b[31mCRITICAL ALERT\u001b[0m";
        string htmlRedBold = GlacierAnsiParser.ToHtml(ansiRedBold);
        Assert.Contains("font-weight:bold", htmlRedBold);
        Assert.Contains("color:#ef4444", htmlRedBold);
        Assert.Contains("CRITICAL ALERT", htmlRedBold);

        string ansiYellowUnderline = "\u001b[4m\u001b[33mWarning Message\u001b[0m";
        string htmlYellowUnderline = GlacierAnsiParser.ToHtml(ansiYellowUnderline);
        Assert.Contains("text-decoration:underline", htmlYellowUnderline);
        Assert.Contains("color:#eab308", htmlYellowUnderline);
        Assert.Contains("Warning Message", htmlYellowUnderline);

        string ansiCyan = "\u001b[36mTelemetry Info\u001b[0m";
        string htmlCyan = GlacierAnsiParser.ToHtml(ansiCyan);
        Assert.Contains("color:#06b6d4", htmlCyan);
        Assert.Contains("Telemetry Info", htmlCyan);

        // 5. Test log entry HTML message generation
        service.AddLog("TEST", ansiGreen);
        var lastLog = service.Logs.Last();
        Assert.Contains("color:#22c55e", lastLog.HtmlMessage);

        // 6. Test HTML encoding prevention of XSS
        string xssRaw = "<script>alert('xss')</script>";
        string xssHtml = GlacierAnsiParser.ToHtml(xssRaw);
        Assert.DoesNotContain("<script>", xssHtml);
        Assert.Contains("&lt;script&gt;", xssHtml);
    }

    // =========================================================================
    // 5. DocTree Line Navigation & Formatted Document Preview
    // =========================================================================

    [Fact]
    public void DocTreeLineNavigation_ComplexMultiSectionDocument_AstAndFormattedPreview()
    {
        var service = new GlacierDocTreePreviewService();

        string complexMarkdown = @"---
title: Glacier Agent Studio Production Architecture
version: 2.1.0
author: Glacier Team
tags:
  - net10
  - mcp
  - antigravity
---

# 🚀 Glacier Agent Studio (Antigravity 2)
The next generation high-performance autonomous developer cockpit.

## Architecture Overview
System architecture spans three cohesive tiers.

### Presentation Tier
Blazor interactive server components.

### Autonomous Agent Runtime
ReAct multi-turn loop and streaming client.

## Component Specifications
Detailed matrix of components:

| Component | Assembly | Target |
| :--- | :--- | :--- |
| Agent Engine | Glacier.Showcase.dll | .NET 10 |
| DocTree | Glacier.DocTree.dll | .NET 10 |
| Mcp Client | Glacier.Showcase.dll | .NET 10 |

```csharp
public class StdioMcpDemo
{
    public void Ping() => Console.WriteLine(""Pong"");
}
```

> Note: All hot paths maintain strict zero heap allocation.

## Verification & Compliance
Strict verification gates:
- Zero Secrets Policy
- 100% Passing Tests
- Release Mode Compilation";

        service.SetDocument("ComplexDoc.md", complexMarkdown, "docs/ComplexDoc.md");
        var doc = service.ActiveDocument;

        Assert.NotNull(doc);
        Assert.Equal("ComplexDoc.md", doc.Title);
        Assert.True(doc.WordCount > 50, "Document word count should be accurately counted.");
        Assert.True(doc.EstimatedTokens > 50, "Document estimated tokens should be calculated.");
        int lineCount = doc.RawContent.Split('\n').Length;
        Assert.True(lineCount > 30, "Document line count should reflect markdown line count.");

        // Verify AST Outline items
        Assert.True(doc.Outline.Count >= 5, "Outline should contain all major headings.");

        // Outline Item 0: Level 1 Title at line 11 (after frontmatter)
        var h1 = doc.Outline[0];
        Assert.Equal("🚀 Glacier Agent Studio (Antigravity 2)", h1.Title);
        Assert.Equal(1, h1.Level);
        Assert.Equal(11, h1.LineNumber);

        // Outline Item 1: Level 2 Architecture Overview at line 14
        var h2_1 = doc.Outline[1];
        Assert.Equal("Architecture Overview", h2_1.Title);
        Assert.Equal(2, h2_1.Level);
        Assert.Equal(14, h2_1.LineNumber);

        // Outline Item 2: Level 3 Presentation Tier at line 17
        var h3_1 = doc.Outline[2];
        Assert.Equal("Presentation Tier", h3_1.Title);
        Assert.Equal(3, h3_1.Level);
        Assert.Equal(17, h3_1.LineNumber);

        // Outline Item 3: Level 3 Autonomous Agent Runtime at line 20
        var h3_2 = doc.Outline[3];
        Assert.Equal("Autonomous Agent Runtime", h3_2.Title);
        Assert.Equal(3, h3_2.Level);
        Assert.Equal(20, h3_2.LineNumber);

        // Outline Item 4: Level 2 Component Specifications at line 23
        var h2_2 = doc.Outline[4];
        Assert.Equal("Component Specifications", h2_2.Title);
        Assert.Equal(2, h2_2.Level);
        Assert.Equal(23, h2_2.LineNumber);

        // Verify Formatted HTML rendering
        string html = doc.FormattedHtml;
        Assert.NotEmpty(html);

        // Must contain line navigation heading anchors
        Assert.Contains("id=\"heading-line-11\"", html);
        Assert.Contains("id=\"heading-line-14\"", html);
        Assert.Contains("id=\"heading-line-17\"", html);
        Assert.Contains("id=\"heading-line-20\"", html);
        Assert.Contains("id=\"heading-line-23\"", html);

        // Must contain table markup
        Assert.Contains("<table", html);
        Assert.Contains("<thead", html);
        Assert.Contains("<tbody", html);
        Assert.Contains("Glacier.Showcase.dll", html);

        // Must contain code block markup
        Assert.Contains("<pre><code", html);
        Assert.Contains("StdioMcpDemo", html);

        // Must contain blockquote markup
        Assert.Contains("<blockquote", html);
        Assert.Contains("strict zero heap allocation", html);
    }

    // =========================================================================
    // 6. Dynamic Hardware Telemetry via DeviceManager
    // =========================================================================

    [Fact]
    public void DynamicHardwareTelemetry_DeviceManager_EnumerateAndVerifyOptimalDevice()
    {
        // Query physical hardware compute devices
        var devices = DeviceManager.GetDevices();
        Assert.NotNull(devices);
        Assert.NotEmpty(devices);

        // Verify properties on every enumerated device
        foreach (var device in devices)
        {
            Assert.False(string.IsNullOrWhiteSpace(device.Id), "Device Id must not be empty.");
            Assert.False(string.IsNullOrWhiteSpace(device.Name), "Device Name must not be empty.");
            Assert.True(device.Index >= 0, "Device Index must be non-negative.");
            Assert.True(device.DedicatedVramBytes >= 0, "Dedicated VRAM must be >= 0.");
            Assert.True(device.SharedVramBytes >= 0, "Shared VRAM must be >= 0.");
            Assert.NotNull(device.SupportedEngines);
            Assert.NotEmpty(device.SupportedEngines);
        }

        // Query optimal computing device
        var optimalDevice = DeviceManager.GetOptimalDevice();
        Assert.NotNull(optimalDevice);
        Assert.Contains(optimalDevice, devices);
        Assert.False(string.IsNullOrWhiteSpace(optimalDevice.Name));

        // Test device resolution queries
        var resolvedAuto = DeviceManager.ResolveDevice("auto");
        Assert.Equal(optimalDevice.Id, resolvedAuto.Id);

        var resolvedCpu = DeviceManager.ResolveDevice("cpu");
        Assert.NotNull(resolvedCpu);
        Assert.Equal(GpuVendor.Cpu, resolvedCpu.Vendor);

        // Verify hardware acceleration intrinsics on host CPU
        Assert.True(Vector128.IsHardwareAccelerated, "Host CPU must support Vector128.");
        Assert.True(Vector256.IsHardwareAccelerated, "Host CPU must support Vector256.");
        Assert.True(Environment.ProcessorCount >= 2, "Host machine must have at least 2 logical cores.");
    }

    // =========================================================================
    // 7. Resilient Tool Argument Sanitization Under Malformed Model Outputs
    // =========================================================================

    [Fact]
    public async Task AutonomousAgentCycle_ResilientToolArgumentSanitization_UnderMalformedJson()
    {
        int turnCount = 0;

        var (engine, state, consoleService, docTree, mcp) = CreateTestHarness(async (req, ct) =>
        {
            turnCount++;

            if (turnCount == 1)
            {
                // Model emits arguments with markdown fences and unescaped Windows paths:
                // ```json
                // { "command": "Write-Output 'SanitizeSuccess'" }
                // ```
                string malformedArgs = "```json\n{\n  \"command\": \"Write-Output 'SanitizeSuccess'\"\n}\n```";
                var toolDelta = new JsonObject
                {
                    ["id"] = "chatcmpl-t1",
                    ["choices"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["index"] = 0,
                            ["delta"] = new JsonObject
                            {
                                ["tool_calls"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["index"] = 0,
                                        ["id"] = "call_sanitize_01",
                                        ["type"] = "function",
                                        ["function"] = new JsonObject
                                        {
                                            ["name"] = "run_command",
                                            ["arguments"] = malformedArgs
                                        }
                                    }
                                }
                            }
                        }
                    }
                };

                string sseTurn1 =
                    "data: {\"id\":\"chatcmpl-t1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Running command with sanitized arguments.\\n\"}}]}\n\n" +
                    $"data: {toolDelta.ToJsonString()}\n\n" +
                    "data: {\"id\":\"chatcmpl-t1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"completion_tokens\":30,\"prompt_tokens\":100,\"total_tokens\":130}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn1);
            }
            else
            {
                string sseTurn2 =
                    "data: {\"id\":\"chatcmpl-t2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Sanitized command successfully executed.\\n\"}}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-t2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"completion_tokens\":12,\"prompt_tokens\":150,\"total_tokens\":162}}\n\n" +
                    "data: [DONE]\n\n";

                return CreateSseResponse(sseTurn2);
            }
        });

        var session = state.CreateNewSession("e2e_test_Sanitization");

        // Must complete without throwing JsonException
        await engine.ProcessUserMessageAsync("Run command with raw markdown formatting.");

        Assert.Equal(2, turnCount);
        Assert.Equal(AgentExecutionStatus.Completed, state.ExecutionStatus);

        var assistantMsg = session.Messages.First(m => m.Role == "assistant" && m.ToolCalls.Count > 0);
        Assert.Single(assistantMsg.ToolCalls);
        var toolRecord = assistantMsg.ToolCalls[0];
        Assert.Equal("run_command", toolRecord.ToolName);
        Assert.NotNull(toolRecord.ResultOutput);
        Assert.Contains("SanitizeSuccess", toolRecord.ResultOutput);
    }

    // =========================================================================
    // 8. Graceful Cancellation Handling Halts Execution and Preserves History
    // =========================================================================

    [Fact]
    public async Task AutonomousAgentCycle_GracefulCancellation_HaltsExecutionAndPreservesHistory()
    {
        var streamStartedTcs = new TaskCompletionSource<bool>();

        var (engine, state, consoleService, docTree, mcp) = CreateTestHarness(async (req, ct) =>
        {
            // Signal that streaming started
            streamStartedTcs.TrySetResult(true);

            // Emulate slow/long stream that hangs until cancelled
            var hangingStream = new CancellableHangingStream("data: {\"id\":\"chatcmpl-slow\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"First chunk emitted...\"}}]}\n\n");
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Content = new StreamContent(hangingStream);
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        var session = state.CreateNewSession("e2e_test_Cancellation");

        var executionTask = engine.ProcessUserMessageAsync("Slow task to be cancelled.");

        // Wait until streaming starts
        await streamStartedTcs.Task;

        // Cancel execution
        engine.CancelCurrentExecution();

        // Wait for execution task to complete
        await executionTask;

        // Verify status transitioned to Cancelled
        Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
        Assert.Contains("Cancelled", state.StatusText);

        // Verify message history preserved
        Assert.NotEmpty(session.Messages);
        var assistantMsg = session.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(assistantMsg);
        Assert.Contains("cancelled", assistantMsg.Content, StringComparison.OrdinalIgnoreCase);
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

            // Hang indefinitely until cancelled
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    // =========================================================================
    // 9. Endpoint Error Diagnostics: Honest Error Reporting (Zero Mock Fallback)
    // =========================================================================

    [Fact]
    public async Task AutonomousAgentCycle_EndpointError_ProvidesHonestDiagnosticNotice()
    {
        var (engine, state, consoleService, docTree, mcp) = CreateTestHarness((req, ct) =>
        {
            // Emulate endpoint 503 Service Unavailable failure
            var resp = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("Ollama server at 127.0.0.1:11434 is offline or busy.")
            };
            return Task.FromResult(resp);
        });

        var session = state.CreateNewSession("e2e_test_EndpointError");

        await engine.ProcessUserMessageAsync("Analyze current repository git status.");

        // Assert honest endpoint error: MUST NOT generate synthetic keyword fallback tools!
        var assistantMsg = session.Messages.LastOrDefault(m => m.Role == "assistant");
        Assert.NotNull(assistantMsg);
        Assert.True(assistantMsg.IsError, "Assistant message must be marked with IsError = true.");
        Assert.Contains("Model Endpoint Error", assistantMsg.Content);
        Assert.Contains("503", assistantMsg.Content);
        Assert.Empty(assistantMsg.ToolCalls); // No fake tool calls generated!

        // Verify console logs contain the structured endpoint error
        Assert.Contains(consoleService.Logs, l => l.IsError && l.Message.Contains("503"));
    }
}
