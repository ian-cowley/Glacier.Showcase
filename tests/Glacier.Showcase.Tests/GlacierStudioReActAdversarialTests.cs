using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

[Collection("GlacierStudioSessionTests")]
public class GlacierStudioReActAdversarialTests
{
    private static GlacierAgentEngine CreateTestAgentEngine()
    {
        return new GlacierAgentEngine(
            new HttpClient(),
            new GlacierStudioState(),
            new GlacierConsoleService(),
            new GlacierDocTreePreviewService(),
            new GlacierMcpManager());
    }

    private static List<JsonObject> InvokeBuildMessagesPayload(GlacierAgentEngine engine, StudioSession session)
    {
        var method = typeof(GlacierAgentEngine).GetMethod(
            "BuildMessagesPayload",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);
        var result = method.Invoke(engine, new object[] { session });
        Assert.NotNull(result);
        return (List<JsonObject>)result;
    }

    #region BuildMessagesPayload Adversarial Tests

    [Fact]
    public void BuildMessagesPayload_UserAssistantToolSequence_GeneratesToolCallsPrecedingToolMessage()
    {
        var engine = CreateTestAgentEngine();
        var session = new StudioSession
        {
            Id = "session_react_01",
            Title = "ReAct Sequence Test"
        };

        // Turn 1: User Request
        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "List files in the working directory",
            Timestamp = DateTime.Now
        });

        // Turn 2: Assistant decides to call tool
        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "I will list the directory contents using run_command.",
            Timestamp = DateTime.Now
        };
        assistantMsg.ToolCalls.Add(new AgentToolCallRecord
        {
            Id = "call_react_999",
            ToolName = "run_command",
            ArgumentsJson = "{\"command\":\"Get-ChildItem\"}"
        });
        session.Messages.Add(assistantMsg);

        // Turn 3: Tool Execution Response
        session.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = "call_react_999",
            Content = "Mode LastWriteTime Length Name\n-a--- 2026-10-11 512 README.md",
            Timestamp = DateTime.Now
        });

        // Turn 4: Assistant Final Answer
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "The directory contains README.md.",
            Timestamp = DateTime.Now
        });

        var payload = InvokeBuildMessagesPayload(engine, session);

        // Assert payload length: system + user + assistant(with tools) + tool + assistant(final)
        Assert.Equal(5, payload.Count);

        // 0: System Message
        Assert.Equal("system", payload[0]["role"]?.GetValue<string>());
        Assert.Contains("Glacier Antigravity Engineering Agent", payload[0]["content"]?.GetValue<string>());

        // 1: User Message
        Assert.Equal("user", payload[1]["role"]?.GetValue<string>());
        Assert.Equal("List files in the working directory", payload[1]["content"]?.GetValue<string>());

        // 2: Assistant Message with tool_calls
        Assert.Equal("assistant", payload[2]["role"]?.GetValue<string>());
        Assert.Equal("I will list the directory contents using run_command.", payload[2]["content"]?.GetValue<string>());
        var toolCallsNode = payload[2]["tool_calls"] as JsonArray;
        Assert.NotNull(toolCallsNode);
        Assert.Single(toolCallsNode);

        var firstCall = toolCallsNode[0] as JsonObject;
        Assert.NotNull(firstCall);
        Assert.Equal("call_react_999", firstCall["id"]?.GetValue<string>());
        Assert.Equal("function", firstCall["type"]?.GetValue<string>());

        var funcObj = firstCall["function"] as JsonObject;
        Assert.NotNull(funcObj);
        Assert.Equal("run_command", funcObj["name"]?.GetValue<string>());
        Assert.Equal("{\"command\":\"Get-ChildItem\"}", funcObj["arguments"]?.GetValue<string>());

        // 3: Tool Message
        Assert.Equal("tool", payload[3]["role"]?.GetValue<string>());
        Assert.Equal("call_react_999", payload[3]["tool_call_id"]?.GetValue<string>());
        Assert.Contains("README.md", payload[3]["content"]?.GetValue<string>());

        // CRITICAL INVARIANT: Assistant message (index 2) must strictly precede tool message (index 3),
        // and tool_call_id must match the assistant tool_calls id.
        Assert.Equal(firstCall["id"]?.GetValue<string>(), payload[3]["tool_call_id"]?.GetValue<string>());

        // 4: Assistant Final Answer (no tool calls)
        Assert.Equal("assistant", payload[4]["role"]?.GetValue<string>());
        Assert.Equal("The directory contains README.md.", payload[4]["content"]?.GetValue<string>());
        Assert.False(payload[4].ContainsKey("tool_calls"));
    }

    [Fact]
    public void BuildMessagesPayload_AssistantPureToolCallWithoutContent_SerializesNullContentAndToolCalls()
    {
        var engine = CreateTestAgentEngine();
        var session = new StudioSession();

        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Read configuration"
        });

        // Assistant emits tool call with null / empty Content (standard OpenAI streaming behavior)
        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "",
            ToolCalls = new List<AgentToolCallRecord>
            {
                new AgentToolCallRecord
                {
                    Id = "call_pure_tool_01",
                    ToolName = "read_file",
                    ArgumentsJson = "{\"path\":\"config.json\"}"
                }
            }
        };
        session.Messages.Add(assistantMsg);

        session.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = "call_pure_tool_01",
            Content = "{\"mode\":\"production\"}"
        });

        var payload = InvokeBuildMessagesPayload(engine, session);
        Assert.Equal(4, payload.Count);

        // Assistant object must serialize content as null (or null JsonNode), not throw or omit
        var assistantObj = payload[2];
        Assert.Equal("assistant", assistantObj["role"]?.GetValue<string>());
        Assert.Null(assistantObj["content"]);

        var toolCalls = assistantObj["tool_calls"] as JsonArray;
        Assert.NotNull(toolCalls);
        Assert.Single(toolCalls);
        Assert.Equal("call_pure_tool_01", toolCalls[0]?["id"]?.GetValue<string>());

        // Following tool message matches
        Assert.Equal("tool", payload[3]["role"]?.GetValue<string>());
        Assert.Equal("call_pure_tool_01", payload[3]["tool_call_id"]?.GetValue<string>());
    }

    [Fact]
    public void BuildMessagesPayload_MultipleParallelToolCalls_EmitsAllToolCallsInSingleAssistantMessage()
    {
        var engine = CreateTestAgentEngine();
        var session = new StudioSession();

        session.Messages.Add(new ChatMessage { Role = "user", Content = "Process batch" });

        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "Executing parallel tool calls"
        };
        assistantMsg.ToolCalls.Add(new AgentToolCallRecord { Id = "call_p1", ToolName = "read_file", ArgumentsJson = "{\"path\":\"a.txt\"}" });
        assistantMsg.ToolCalls.Add(new AgentToolCallRecord { Id = "call_p2", ToolName = "read_file", ArgumentsJson = "{\"path\":\"b.txt\"}" });
        assistantMsg.ToolCalls.Add(new AgentToolCallRecord { Id = "call_p3", ToolName = "run_command", ArgumentsJson = "{\"command\":\"dotnet --info\"}" });
        session.Messages.Add(assistantMsg);

        session.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call_p1", Content = "content a" });
        session.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call_p2", Content = "content b" });
        session.Messages.Add(new ChatMessage { Role = "tool", ToolCallId = "call_p3", Content = ".NET 10.0" });

        var payload = InvokeBuildMessagesPayload(engine, session);

        // System + User + Assistant + 3 Tools = 6
        Assert.Equal(6, payload.Count);

        var toolCalls = payload[2]["tool_calls"] as JsonArray;
        Assert.NotNull(toolCalls);
        Assert.Equal(3, toolCalls.Count);

        Assert.Equal("call_p1", toolCalls[0]?["id"]?.GetValue<string>());
        Assert.Equal("call_p2", toolCalls[1]?["id"]?.GetValue<string>());
        Assert.Equal("call_p3", toolCalls[2]?["id"]?.GetValue<string>());

        Assert.Equal("call_p1", payload[3]["tool_call_id"]?.GetValue<string>());
        Assert.Equal("call_p2", payload[4]["tool_call_id"]?.GetValue<string>());
        Assert.Equal("call_p3", payload[5]["tool_call_id"]?.GetValue<string>());
    }

    [Fact]
    public void BuildMessagesPayload_MissingIds_GeneratesValidFallbacksWithoutThrowing()
    {
        var engine = CreateTestAgentEngine();
        var session = new StudioSession();

        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "Calling tool with empty ID"
        };
        assistantMsg.ToolCalls.Add(new AgentToolCallRecord
        {
            Id = "", // Missing ID
            ToolName = "run_command",
            ArgumentsJson = "{}"
        });
        session.Messages.Add(assistantMsg);

        // Tool message with null ToolCallId
        session.Messages.Add(new ChatMessage
        {
            Role = "tool",
            ToolCallId = null,
            Content = "output"
        });

        var payload = InvokeBuildMessagesPayload(engine, session);

        var toolCalls = payload[1]["tool_calls"] as JsonArray;
        Assert.NotNull(toolCalls);
        string? genId = toolCalls[0]?["id"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(genId));
        Assert.StartsWith("call_", genId);

        // Tool message fallback
        Assert.Equal("call_0", payload[2]["tool_call_id"]?.GetValue<string>());
    }

    #endregion

    #region ToolArgumentSanitizer Adversarial Tests

    [Fact]
    public void ToolArgumentSanitizer_RawUnescapedWindowsPaths_ParsesCleanlyWithoutThrowing()
    {
        // Raw unescaped Windows paths where backslashes like \U, \t, \d, \f normally cause JsonReaderException
        string rawJsonWithWindowsPath = "{\"path\":\"C:\\Users\\test\\dir\\file.txt\"}";

        var root = ToolArgumentSanitizer.ParseAndSanitize(rawJsonWithWindowsPath);

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("path", out var pathProp));
        Assert.Equal("C:\\Users\\test\\dir\\file.txt", pathProp.GetString());

        // Multi-level Windows path with project drive
        string multiPath = "{\"source\":\"D:\\Projects\\Glacier\\src\\main.cs\",\"dest\":\"E:\\Backup\\2026\\data.zip\"}";
        var root2 = ToolArgumentSanitizer.ParseAndSanitize(multiPath);
        Assert.Equal(JsonValueKind.Object, root2.ValueKind);
        Assert.Equal("D:\\Projects\\Glacier\\src\\main.cs", root2.GetProperty("source").GetString());
        Assert.Equal("E:\\Backup\\2026\\data.zip", root2.GetProperty("dest").GetString());
    }

    [Fact]
    public void ToolArgumentSanitizer_MarkdownFencedBlocks_StripsFencesAndParses()
    {
        // Standard markdown block with json tag
        string mdBlock = "```json\n{\"command\":\"dir\"}\n```";
        var root1 = ToolArgumentSanitizer.ParseAndSanitize(mdBlock);
        Assert.Equal(JsonValueKind.Object, root1.ValueKind);
        Assert.Equal("dir", root1.GetProperty("command").GetString());

        // Markdown block with CRLF
        string mdCrlf = "```json\r\n{\"command\":\"pwsh -c Get-Process\"}\r\n```";
        var root2 = ToolArgumentSanitizer.ParseAndSanitize(mdCrlf);
        Assert.Equal("pwsh -c Get-Process", root2.GetProperty("command").GetString());

        // Markdown block without language tag
        string mdNoLang = "```\n{\"path\":\"README.md\"}\n```";
        var root3 = ToolArgumentSanitizer.ParseAndSanitize(mdNoLang);
        Assert.Equal("README.md", root3.GetProperty("path").GetString());

        // Uppercase JSON tag
        string mdUpper = "```JSON\n{\"flag\":true}\n```";
        var root4 = ToolArgumentSanitizer.ParseAndSanitize(mdUpper);
        Assert.True(root4.GetProperty("flag").GetBoolean());

        // Truncated markdown fence (missing closing fence from model stream cutoff)
        string mdTruncated = "```json\n{\"command\":\"echo 42\"}";
        var root5 = ToolArgumentSanitizer.ParseAndSanitize(mdTruncated);
        Assert.Equal("echo 42", root5.GetProperty("command").GetString());
    }

    [Fact]
    public void ToolArgumentSanitizer_RawNonJsonStrings_FallsBackResilientlyWithoutThrowing()
    {
        // Raw command strings emitted by model without JSON formatting
        string[] rawInputs =
        {
            "dir /s /b",
            "git status",
            "dotnet test --no-build -c Release",
            "C:\\Users\\test\\sample.txt",
            "Just a plain sentence with special chars: !@#$%^&*()_+"
        };

        foreach (var raw in rawInputs)
        {
            var root = ToolArgumentSanitizer.ParseAndSanitize(raw);
            Assert.Equal(JsonValueKind.Object, root.ValueKind);

            // Resilient fallback exposes command, path, raw, and arguments
            Assert.True(root.TryGetProperty("raw", out var rawProp));
            Assert.Equal(raw, rawProp.GetString());

            Assert.True(root.TryGetProperty("command", out var cmdProp));
            Assert.Equal(raw, cmdProp.GetString());

            Assert.True(root.TryGetProperty("path", out var pathProp));
            Assert.Equal(raw, pathProp.GetString());

            Assert.True(root.TryGetProperty("arguments", out var argsProp));
            Assert.Equal(raw, argsProp.GetString());
        }
    }

    [Fact]
    public void ToolArgumentSanitizer_EmptyAndNullInputs_ReturnsEmptyObject()
    {
        var rootNull = ToolArgumentSanitizer.ParseAndSanitize(null!);
        Assert.Equal(JsonValueKind.Object, rootNull.ValueKind);
        Assert.Empty(rootNull.EnumerateObject());

        var rootEmpty = ToolArgumentSanitizer.ParseAndSanitize("");
        Assert.Equal(JsonValueKind.Object, rootEmpty.ValueKind);
        Assert.Empty(rootEmpty.EnumerateObject());

        var rootWhitespace = ToolArgumentSanitizer.ParseAndSanitize("   \t\r\n   ");
        Assert.Equal(JsonValueKind.Object, rootWhitespace.ValueKind);
        Assert.Empty(rootWhitespace.EnumerateObject());
    }

    [Fact]
    public void ToolArgumentSanitizer_AlreadyEscapedJson_PreservesExactStructure()
    {
        string validJson = "{\"path\":\"C:\\\\Users\\\\test\\\\file.txt\",\"number\":42,\"flag\":true}";
        var root = ToolArgumentSanitizer.ParseAndSanitize(validJson);

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("C:\\Users\\test\\file.txt", root.GetProperty("path").GetString());
        Assert.Equal(42, root.GetProperty("number").GetInt32());
        Assert.True(root.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void ToolArgumentSanitizer_MalformedJsonPrimitivesAndArrays_WrapsInFallback()
    {
        // JSON array instead of object
        var rootArr = ToolArgumentSanitizer.ParseAndSanitize("[1, 2, 3]");
        Assert.Equal(JsonValueKind.Object, rootArr.ValueKind);
        Assert.Equal("[1, 2, 3]", rootArr.GetProperty("raw").GetString());

        // JSON primitive number
        var rootNum = ToolArgumentSanitizer.ParseAndSanitize("12345");
        Assert.Equal(JsonValueKind.Object, rootNum.ValueKind);
        Assert.Equal("12345", rootNum.GetProperty("raw").GetString());

        // Malformed JSON (unquoted keys / syntax error)
        var rootMalformed = ToolArgumentSanitizer.ParseAndSanitize("{ command: dir, count: 5 }");
        Assert.Equal(JsonValueKind.Object, rootMalformed.ValueKind);
        Assert.Equal("{ command: dir, count: 5 }", rootMalformed.GetProperty("raw").GetString());
    }

    [Fact]
    public void ToolArgumentSanitizer_AdversarialDeepStress_LargeInputsAndSpecialCharacters()
    {
        // 50KB long string argument
        string largeInput = "{\"command\":\"echo " + new string('A', 50000) + "\"}";
        var rootLarge = ToolArgumentSanitizer.ParseAndSanitize(largeInput);
        Assert.Equal(JsonValueKind.Object, rootLarge.ValueKind);
        Assert.StartsWith("echo AAAA", rootLarge.GetProperty("command").GetString()!);

        // Mixed forward and backward slashes with spaces
        string mixed = "{\"source\":\"C:\\Program Files\\Glacier Suite\\data.bin\",\"target\":\"/var/log/glacier.log\"}";
        var rootMixed = ToolArgumentSanitizer.ParseAndSanitize(mixed);
        Assert.Equal(JsonValueKind.Object, rootMixed.ValueKind);
        Assert.Equal("C:\\Program Files\\Glacier Suite\\data.bin", rootMixed.GetProperty("source").GetString());
        Assert.Equal("/var/log/glacier.log", rootMixed.GetProperty("target").GetString());

        // Unicode and emojis
        string unicode = "{\"message\":\"❄️ Glacier Antigravity 2 - 🚀 High Performance C#\"}";
        var rootUnicode = ToolArgumentSanitizer.ParseAndSanitize(unicode);
        Assert.Equal("❄️ Glacier Antigravity 2 - 🚀 High Performance C#", rootUnicode.GetProperty("message").GetString());
    }

    #endregion
}
