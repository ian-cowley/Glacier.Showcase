using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Dedicated Unit and Component Tests for GlacierAgentEngine.
/// Verifies slash commands, prompt processing, tool schema definitions, cancellation,
/// and streaming response lifecycle.
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierAgentEngineTests
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

    private static (GlacierAgentEngine engine, GlacierStudioState state, GlacierConsoleService console, GlacierDocTreePreviewService docTree, GlacierMcpManager mcp)
        CreateTestHarness(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? httpHandler = null)
    {
        var handler = new MockHttpHandler(httpHandler ?? ((req, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var httpClient = new HttpClient(handler);
        var state = new GlacierStudioState();
        var consoleService = new GlacierConsoleService();
        var docPreviewService = new GlacierDocTreePreviewService();
        var mcpManager = new GlacierMcpManager();

        var engine = new GlacierAgentEngine(
            httpClient,
            state,
            consoleService,
            docPreviewService,
            mcpManager);

        return (engine, state, consoleService, docPreviewService, mcpManager);
    }

    [Fact]
    public async Task ProcessUserMessage_NullOrWhitespacePrompt_DoesNotAddMessage()
    {
        var (engine, state, _, _, _) = CreateTestHarness();
        int initialCount = state.ActiveSession.Messages.Count;

        await engine.ProcessUserMessageAsync("");
        Assert.Equal(initialCount, state.ActiveSession.Messages.Count);

        await engine.ProcessUserMessageAsync("   ");
        Assert.Equal(initialCount, state.ActiveSession.Messages.Count);
    }

    [Fact]
    public async Task SlashCommand_Help_AddsHelpInformationMessage()
    {
        var (engine, state, _, _, _) = CreateTestHarness();
        var session = state.ActiveSession;
        int countBefore = session.Messages.Count;

        await engine.ProcessUserMessageAsync("/help");

        Assert.True(session.Messages.Count > countBefore);
        var lastMsg = session.Messages.Last();
        Assert.Equal("assistant", lastMsg.Role);
        Assert.Contains("/terminal", lastMsg.Content);
        Assert.Contains("/inspect", lastMsg.Content);
        Assert.Contains("/mcp", lastMsg.Content);
        Assert.Contains("/clear", lastMsg.Content);
    }

    [Fact]
    public async Task SlashCommand_Clear_ClearsMessageHistoryAndAddsNotice()
    {
        var (engine, state, _, _, _) = CreateTestHarness();
        var session = state.ActiveSession;

        // Add some dummy messages
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Message 1" });
        session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Message 2" });

        await engine.ProcessUserMessageAsync("/clear");

        Assert.Single(session.Messages);
        Assert.Equal("assistant", session.Messages[0].Role);
        Assert.Contains("Conversation history cleared", session.Messages[0].Content);
    }

    [Fact]
    public async Task SlashCommand_Terminal_ExecutesShellCommandDirectly()
    {
        var (engine, state, consoleService, _, _) = CreateTestHarness();
        var session = state.ActiveSession;

        await engine.ProcessUserMessageAsync("/terminal echo 'Terminal Slash Test'");

        Assert.Equal("console", state.ActiveRightTab);
        var lastMsg = session.Messages.Last();
        Assert.Equal("assistant", lastMsg.Role);
        Assert.Contains("Terminal Slash Test", lastMsg.Content);
        Assert.Contains("Exit code: 0", lastMsg.Content);
    }

    [Fact]
    public async Task SlashCommand_Inspect_LoadsFileIntoDocTreePreview()
    {
        var (engine, state, _, docTree, _) = CreateTestHarness();
        var session = state.ActiveSession;

        // Create a temporary file to inspect
        string tempFile = Path.Combine(Path.GetTempPath(), $"inspect_test_{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(tempFile, "# Header in Temp File\nBody paragraph text.");

        try
        {
            await engine.ProcessUserMessageAsync($"/inspect {tempFile}");

            Assert.Equal("doc", state.ActiveRightTab);
            var lastMsg = session.Messages.Last();
            Assert.Equal("assistant", lastMsg.Role);
            Assert.Contains("Document Preview", lastMsg.Content);
            Assert.Equal(Path.GetFileName(tempFile), docTree.ActiveDocument.Title);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }

    [Fact]
    public async Task SlashCommand_Mcp_ListsConnectedMcpServersAndTools()
    {
        var (engine, state, _, _, mcp) = CreateTestHarness();
        var session = state.ActiveSession;

        await engine.ProcessUserMessageAsync("/mcp");

        Assert.Equal("mcp", state.ActiveRightTab);
        var lastMsg = session.Messages.Last();
        Assert.Equal("assistant", lastMsg.Role);
        Assert.Contains("Connected MCP Servers & Tools", lastMsg.Content);
        Assert.Contains("GlacierVectorEngine", lastMsg.Content);
        Assert.Contains("GlacierGraphEngine", lastMsg.Content);
    }

    [Fact]
    public void CancelCurrentExecution_WhenIdle_TransitionsToCancelledStatus()
    {
        var (engine, state, _, _, _) = CreateTestHarness();

        engine.CancelCurrentExecution();

        Assert.Equal(AgentExecutionStatus.Cancelled, state.ExecutionStatus);
        Assert.Equal("Cancelled by user", state.StatusText);
    }

    [Fact]
    public void GetToolsSchema_ReturnsAllSixCoreToolsWithValidJson()
    {
        var method = typeof(GlacierAgentEngine).GetMethod(
            "GetToolsSchema",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var result = method.Invoke(null, null);
        Assert.NotNull(result);

        var jsonArray = (JsonArray)result;
        Assert.Equal(6, jsonArray.Count);

        var toolNames = jsonArray
            .Select(t => t?["function"]?["name"]?.GetValue<string>())
            .ToList();

        Assert.Contains("run_command", toolNames);
        Assert.Contains("read_file", toolNames);
        Assert.Contains("write_file", toolNames);
        Assert.Contains("list_directory", toolNames);
        Assert.Contains("inspect_document", toolNames);
        Assert.Contains("call_mcp_tool", toolNames);

        // Verify each tool schema has function type and required properties
        foreach (var item in jsonArray)
        {
            var obj = item as JsonObject;
            Assert.NotNull(obj);
            Assert.Equal("function", obj["type"]?.GetValue<string>());
            var func = obj["function"] as JsonObject;
            Assert.NotNull(func);
            Assert.NotNull(func["description"]?.GetValue<string>());
            Assert.NotNull(func["parameters"]);
        }
    }
}
