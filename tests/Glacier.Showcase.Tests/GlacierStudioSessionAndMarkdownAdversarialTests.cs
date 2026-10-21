using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

[Collection("GlacierStudioSessionTests")]
public class GlacierStudioSessionAndMarkdownAdversarialTests : IDisposable
{
    private readonly string _testWorkspaceRoot;
    private readonly string _sessionsDir;
    private readonly string _exportsDir;

    public GlacierStudioSessionAndMarkdownAdversarialTests()
    {
        _testWorkspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        _sessionsDir = Path.Combine(_testWorkspaceRoot, ".glacier", "studio", "sessions");
        _exportsDir = Path.Combine(_testWorkspaceRoot, ".glacier", "studio", "exports");
    }

    public void Dispose()
    {
        // Clean up test session files containing test markers
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

        // Clean up test export files
        if (Directory.Exists(_exportsDir))
        {
            foreach (var file in Directory.GetFiles(_exportsDir, "*adv_test_*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    // =========================================================================
    // 1. Session Disk Persistence Adversarial Tests
    // =========================================================================

    [Fact]
    public void SessionPersistence_SavesAndLoadsSessionFromDisk()
    {
        var state = new GlacierStudioState();
        string testTitle = "adv_test_SessionPersistence_" + Guid.NewGuid().ToString("N")[..6];
        var session = state.CreateNewSession(testTitle);
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Test message prompt" });
        session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Test response content" });
        state.SaveActiveSession();

        string expectedPath = Path.Combine(_sessionsDir, $"{session.Id}.json");
        Assert.True(File.Exists(expectedPath), $"Session file should exist at '{expectedPath}'");

        // Verify JSON content is valid and intact
        string json = File.ReadAllText(expectedPath);
        var loaded = JsonSerializer.Deserialize<StudioSession>(json);
        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(testTitle, loaded.Title);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal("Test message prompt", loaded.Messages[0].Content);
        Assert.Equal("Test response content", loaded.Messages[1].Content);

        // Delete session and verify disk file is removed
        state.DeleteSession(session.Id);
        Assert.False(File.Exists(expectedPath), $"Session file should be removed after deletion");
    }

    [Fact]
    public void SessionPersistence_CorruptedJsonFiles_DoNotCrashApplication()
    {
        Directory.CreateDirectory(_sessionsDir);

        // Scenario A: Completely empty file (0 bytes)
        string emptyFile = Path.Combine(_sessionsDir, "adv_test_corrupt_empty.json");
        File.WriteAllText(emptyFile, "");

        // Scenario B: Truncated / malformed JSON syntax
        string malformedFile = Path.Combine(_sessionsDir, "adv_test_corrupt_malformed.json");
        File.WriteAllText(malformedFile, "{\"Title\": \"Broken\", \"Messages\": [{\"Content\": ");

        // Scenario C: Non-JSON binary garbage
        string binaryFile = Path.Combine(_sessionsDir, "adv_test_corrupt_binary.json");
        File.WriteAllBytes(binaryFile, new byte[] { 0x00, 0xFF, 0xFE, 0xDE, 0xAD, 0xBE, 0xEF });

        // Scenario D: Valid JSON but unexpected schema / null ID
        string nullIdFile = Path.Combine(_sessionsDir, "adv_test_corrupt_null_id.json");
        File.WriteAllText(nullIdFile, "{\"Id\": null, \"Title\": \"Null Id Session\"}");

        // Scenario E: Valid JSON array instead of object
        string arrayFile = Path.Combine(_sessionsDir, "adv_test_corrupt_array.json");
        File.WriteAllText(arrayFile, "[1, 2, 3]");

        // Must NOT throw when creating state or calling LoadSessionsFromDisk
        var state = new GlacierStudioState();
        var record = Record.Exception(() => state.LoadSessionsFromDisk());
        Assert.Null(record);

        // State must still have at least the default session and remain fully operational
        Assert.NotEmpty(state.Sessions);
        Assert.NotNull(state.ActiveSession);
    }

    [Fact]
    public void SessionPersistence_ModifyingSession_UpdatesDiskContent()
    {
        var state = new GlacierStudioState();
        string testTitle = "adv_test_ModifyingSession_" + Guid.NewGuid().ToString("N")[..6];
        var session = state.CreateNewSession(testTitle);
        string expectedPath = Path.Combine(_sessionsDir, $"{session.Id}.json");

        // Initial save
        Assert.True(File.Exists(expectedPath));

        // Modify session
        session.Title = testTitle + "_Updated";
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Modified query" });
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Modified answer",
            ToolCalls = new List<AgentToolCallRecord>
            {
                new AgentToolCallRecord
                {
                    ToolName = "run_command",
                    ArgumentsJson = "{\"command\":\"dir\"}",
                    ResultOutput = "Directory listing...",
                    IsSuccess = true,
                    Duration = TimeSpan.FromMilliseconds(42)
                }
            }
        });
        state.SaveActiveSession();

        // Re-read file and verify modified content
        string updatedJson = File.ReadAllText(expectedPath);
        var loaded = JsonSerializer.Deserialize<StudioSession>(updatedJson);
        Assert.NotNull(loaded);
        Assert.Equal(testTitle + "_Updated", loaded.Title);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Single(loaded.Messages[1].ToolCalls);
        Assert.Equal("run_command", loaded.Messages[1].ToolCalls[0].ToolName);
        Assert.Equal(42, loaded.Messages[1].ToolCalls[0].Duration.TotalMilliseconds);

        state.DeleteSession(session.Id);
    }

    // =========================================================================
    // 2. Session Search Filtering Adversarial Tests
    // =========================================================================

    [Fact]
    public void SessionSearch_EmptyAndWhitespaceQueries_ReturnsAllSessions()
    {
        var state = new GlacierStudioState();
        int total = state.Sessions.Count;

        state.SessionSearchQuery = "";
        Assert.Equal(total, state.FilteredSessions.Count());

        state.SessionSearchQuery = "   ";
        Assert.Equal(total, state.FilteredSessions.Count());

        state.SessionSearchQuery = "\t\n  ";
        Assert.Equal(total, state.FilteredSessions.Count());
    }

    [Fact]
    public void SessionSearch_MatchesTitleAndMessageContent_CaseInsensitively()
    {
        var state = new GlacierStudioState();
        string uniqueTag = Guid.NewGuid().ToString("N")[..8];

        var s1 = state.CreateNewSession($"Quantum Simulation {uniqueTag}");
        s1.Messages.Add(new ChatMessage { Role = "user", Content = "How to run SIMD?" });

        var s2 = state.CreateNewSession($"General Assistant {uniqueTag}");
        s2.Messages.Add(new ChatMessage { Role = "user", Content = "Explain Eigenvalues in polaris" });

        // Search 1: Match in Title (case insensitive)
        state.SessionSearchQuery = "quAnTum";
        var results1 = state.FilteredSessions.ToList();
        Assert.Contains(results1, s => s.Id == s1.Id);
        Assert.DoesNotContain(results1, s => s.Id == s2.Id);

        // Search 2: Match in Message Content (case insensitive)
        state.SessionSearchQuery = "eiGenValUes";
        var results2 = state.FilteredSessions.ToList();
        Assert.Contains(results2, s => s.Id == s2.Id);
        Assert.DoesNotContain(results2, s => s.Id == s1.Id);

        // Search 3: Match both by common unique tag
        state.SessionSearchQuery = uniqueTag.ToLowerInvariant();
        var results3 = state.FilteredSessions.ToList();
        Assert.Contains(results3, s => s.Id == s1.Id);
        Assert.Contains(results3, s => s.Id == s2.Id);

        // Clean up
        state.DeleteSession(s1.Id);
        state.DeleteSession(s2.Id);
    }

    [Fact]
    public void SessionSearch_AdversarialSpecialCharacters_DoesNotCrash()
    {
        var state = new GlacierStudioState();
        string uniqueId = Guid.NewGuid().ToString("N")[..6];
        var testSession = state.CreateNewSession($"Test Special Chars [Regex] {uniqueId}");
        testSession.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Looking for path C:\\Users\\Default and regex (.*) + [a-z]? with quotes \"hello\" and emoji ❄️"
        });

        var adversarialQueries = new[]
        {
            ".*",
            "[a-z]",
            "(.*)",
            "^$",
            "\\",
            "C:\\Users",
            "\"hello\"",
            "❄️",
            "???",
            "+++",
            "<script>alert(1)</script>",
            "'; DROP TABLE Sessions; --"
        };

        foreach (var query in adversarialQueries)
        {
            state.SessionSearchQuery = query;
            var record = Record.Exception(() => state.FilteredSessions.ToList());
            Assert.Null(record);
        }

        // Specifically test literal matching of regex characters
        state.SessionSearchQuery = "C:\\Users";
        Assert.Contains(state.FilteredSessions, s => s.Id == testSession.Id);

        state.SessionSearchQuery = "❄️";
        Assert.Contains(state.FilteredSessions, s => s.Id == testSession.Id);

        state.SessionSearchQuery = "NonExistentTerm_XYZ_123456";
        Assert.DoesNotContain(state.FilteredSessions, s => s.Id == testSession.Id);

        state.DeleteSession(testSession.Id);
    }

    // =========================================================================
    // 3. GlacierMarkdownRenderer Stress & Adversarial Tests
    // =========================================================================

    [Fact]
    public void MarkdownRenderer_EmptyAndWhitespaceInputs_ReturnsEmptyString()
    {
        Assert.Equal("", GlacierMarkdownRenderer.Render(null));
        Assert.Equal("", GlacierMarkdownRenderer.Render(""));
        Assert.Equal("", GlacierMarkdownRenderer.Render("    "));
        Assert.Equal("", GlacierMarkdownRenderer.Render("\r\n\t  \n"));
    }

    [Fact]
    public void MarkdownRenderer_UnclosedCodeBlocks_DoesNotCrashOrThrow()
    {
        string unclosed = "Here is some code:\n```csharp\nint x = 42;\nConsole.WriteLine(x);\n// missing closing fence";
        var exception = Record.Exception(() => GlacierMarkdownRenderer.Render(unclosed));
        Assert.Null(exception);

        string html = GlacierMarkdownRenderer.Render(unclosed);
        Assert.NotNull(html);
        Assert.Contains("int x = 42;", html);
    }

    [Fact]
    public void MarkdownRenderer_CodeBlockWithoutLanguageTag_DefaultsToText()
    {
        string noLang = "```\nConsole.WriteLine(\"Hello, Glacier!\");\n```";
        string html = GlacierMarkdownRenderer.Render(noLang);

        Assert.Contains("gs-code-block", html);
        Assert.Contains("<span class=\"gs-code-lang\">text</span>", html);
        Assert.Contains("class=\"language-text\"", html);
        Assert.Contains("Console.WriteLine(&quot;Hello, Glacier!&quot;);", html);
        Assert.Contains("gs-code-copy-btn", html);
    }

    [Fact]
    public void MarkdownRenderer_TablesWithUnevenColumns_RendersSafelyWithoutException()
    {
        // Table with 3 header columns, but uneven row columns (1 in row 1, 4 in row 2)
        string unevenTable = @"
| Engine | Throughput | Latency |
| :--- | :--- | :--- |
| Glacier.Polaris |
| Glacier.Tensor | 1500 GFLOPS | 0.8 ms | ExtraColumnIgnored |
| Glacier.Vector | 95K ops/s |
";
        var exception = Record.Exception(() => GlacierMarkdownRenderer.Render(unevenTable));
        Assert.Null(exception);

        string html = GlacierMarkdownRenderer.Render(unevenTable);
        Assert.Contains("gs-markdown-table", html);
        Assert.Contains("<th>Engine</th>", html);
        Assert.Contains("<th>Throughput</th>", html);
        Assert.Contains("<th>Latency</th>", html);
        Assert.Contains("<td>Glacier.Polaris</td>", html);
        Assert.Contains("<td>Glacier.Tensor</td>", html);
        Assert.Contains("<td>1500 GFLOPS</td>", html);
        Assert.Contains("<td>Glacier.Vector</td>", html);
    }

    [Theory]
    [InlineData("<script>alert('XSS')</script>", "&lt;script")]
    [InlineData("<img src=\"x\" onerror=\"alert('XSS')\" />", "&lt;img")]
    [InlineData("<iframe src=\"javascript:alert('XSS')\"></iframe>", "&lt;iframe")]
    [InlineData("<svg onload=\"alert('XSS')\"></svg>", "&lt;svg")]
    [InlineData("# <script>alert('Header XSS')</script>", "&lt;script")]
    [InlineData("| Header <script> | Col 2 |\n| --- | --- |\n| Cell <img src=x> | Val |", "&lt;script")]
    [InlineData("> Blockquote <b onmouseover=\"alert(1)\">evil</b>", "&lt;b")]
    public void MarkdownRenderer_RawHtmlInjection_EscapesDangerousTags(string input, string expectedEscapedPrefix)
    {
        string html = GlacierMarkdownRenderer.Render(input);

        // Raw unescaped dangerous tags must NOT be present
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b ", html, StringComparison.OrdinalIgnoreCase);

        // Escaped prefix must be present
        Assert.Contains(expectedEscapedPrefix, html);
    }

    [Fact]
    public void MarkdownRenderer_JavascriptSchemeInMarkdownLink_IsNotRenderedAsAnchorHref()
    {
        string maliciousLink = "[Malicious Link](javascript:alert('XSS'))";
        string html = GlacierMarkdownRenderer.Render(maliciousLink);

        // Must NOT render an anchor with javascript: protocol
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a ", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MarkdownRenderer_DeeplyNestedLists_RendersWithoutInfiniteLoopOrCrash()
    {
        string deeplyNested = @"
* Item 1
* Item 2
  * Subitem 2.1
  * Subitem 2.2
    * Subitem 2.2.1
* Item 3
1. Ordered 1
2. Ordered 2
  1. Nested Ordered 2.1
3. Ordered 3
";
        var exception = Record.Exception(() => GlacierMarkdownRenderer.Render(deeplyNested));
        Assert.Null(exception);

        string html = GlacierMarkdownRenderer.Render(deeplyNested);
        Assert.Contains("gs-md-ul", html);
        Assert.Contains("gs-md-ol", html);
        Assert.Contains("<li>Item 1</li>", html);
        Assert.Contains("<li>Ordered 1</li>", html);
    }

    [Fact]
    public void MarkdownRenderer_StandardMarkdownFeatures_RendersCorrectHtml()
    {
        string md = @"# Heading 1
## Heading 2
### Heading 3

This is **bold text** and *italic text* and `inline code` and ~~strikethrough~~.

Here is a [Glacier Link](https://github.com/ian-cowley/PolarsPlus).

> This is a blockquote.

---
";
        string html = GlacierMarkdownRenderer.Render(md);

        Assert.Contains("<h1 class=\"gs-md-h1\">Heading 1</h1>", html);
        Assert.Contains("<h2 class=\"gs-md-h2\">Heading 2</h2>", html);
        Assert.Contains("<h3 class=\"gs-md-h3\">Heading 3</h3>", html);
        Assert.Contains("<strong>bold text</strong>", html);
        Assert.Contains("<em>italic text</em>", html);
        Assert.Contains("<code class=\"gs-inline-code\">inline code</code>", html);
        Assert.Contains("<del>strikethrough</del>", html);
        Assert.Contains("<a href=\"https://github.com/ian-cowley/PolarsPlus\" target=\"_blank\" rel=\"noopener noreferrer\" class=\"gs-md-link\">Glacier Link</a>", html);
        Assert.Contains("<blockquote class=\"gs-md-blockquote\">", html);
        Assert.Contains("<hr class=\"gs-md-hr\"/>", html);
    }

    // =========================================================================
    // 4. Session Export Adversarial Tests
    // =========================================================================

    [Fact]
    public void SessionExport_ToJson_RoundTripsAccurately()
    {
        var state = new GlacierStudioState();
        var session = state.CreateNewSession("adv_test_ExportJson_" + Guid.NewGuid().ToString("N")[..6]);
        session.ModelSnapshot = "Glacier.Inference: qwen2.5-coder-7b";
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Run benchmark" });
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Results completed.",
            ThinkingContent = "Step 1: check GPU. Step 2: run GEMM.",
            ToolCalls = new List<AgentToolCallRecord>
            {
                new AgentToolCallRecord
                {
                    ToolName = "run_command",
                    ArgumentsJson = "{\"command\":\"dotnet test\"}",
                    ResultOutput = "Passed: 46",
                    IsSuccess = true,
                    Duration = TimeSpan.FromSeconds(1.5)
                }
            }
        });

        string json = state.ExportActiveSessionAsJson();
        Assert.NotEmpty(json);

        var restored = JsonSerializer.Deserialize<StudioSession>(json);
        Assert.NotNull(restored);
        Assert.Equal(session.Id, restored.Id);
        Assert.Equal(session.Title, restored.Title);
        Assert.Equal(session.ModelSnapshot, restored.ModelSnapshot);
        Assert.Equal(2, restored.Messages.Count);
        Assert.Equal("Step 1: check GPU. Step 2: run GEMM.", restored.Messages[1].ThinkingContent);
        Assert.Single(restored.Messages[1].ToolCalls);
        Assert.Equal("dotnet test", JsonDocument.Parse(restored.Messages[1].ToolCalls[0].ArgumentsJson).RootElement.GetProperty("command").GetString());
        Assert.Equal("Passed: 46", restored.Messages[1].ToolCalls[0].ResultOutput);

        state.DeleteSession(session.Id);
    }

    [Fact]
    public void SessionExport_ToMarkdown_IncludesAllMetadataThinkingAndTools()
    {
        var state = new GlacierStudioState();
        var session = state.CreateNewSession("adv_test_ExportMd_" + Guid.NewGuid().ToString("N")[..6]);
        session.ModelSnapshot = "⚡ Ollama (llama3.2)";
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Perform code review." });
        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "All checks passed.",
            ThinkingContent = "Analyzing code structure and edge cases...",
            ToolCalls = new List<AgentToolCallRecord>
            {
                new AgentToolCallRecord
                {
                    ToolName = "read_file",
                    ArgumentsJson = "{\"path\": \"src/Program.cs\"}",
                    ResultOutput = "public static void Main() {}",
                    IsSuccess = true,
                    Duration = TimeSpan.FromMilliseconds(120)
                }
            }
        });

        string markdown = state.ExportActiveSessionAsMarkdown();

        // Verify document structure
        Assert.StartsWith($"# {session.Title}", markdown);
        Assert.Contains($"> **Model**: {session.ModelSnapshot}", markdown);
        Assert.Contains("> **Messages**: 2", markdown);
        Assert.Contains("### 👤 User", markdown);
        Assert.Contains("Perform code review.", markdown);
        Assert.Contains("### 🤖 Assistant (Glacier Agent)", markdown);
        Assert.Contains("<details>", markdown);
        Assert.Contains("<summary>🧠 Thought Process</summary>", markdown);
        Assert.Contains("Analyzing code structure and edge cases...", markdown);
        Assert.Contains("**Tool Invocation**: `read_file` (120 ms)", markdown);
        Assert.Contains("```json\r\n{\"path\": \"src/Program.cs\"}\r\n```", markdown.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"));
        Assert.Contains("public static void Main() {}", markdown);
        Assert.Contains("All checks passed.", markdown);

        state.DeleteSession(session.Id);
    }

    [Fact]
    public void SessionExport_ToDisk_SanitizesInvalidPathCharactersInTitle()
    {
        var state = new GlacierStudioState();
        // Title containing all forbidden Windows filesystem characters: < > : " / \ | ? *
        string dangerousTitle = "adv_test_InvalidChars: <Tag> \"Quote\" /Slash \\Back ?Question *Star |Pipe";
        var session = state.CreateNewSession(dangerousTitle);

        // Export JSON to disk
        string jsonFilePath = state.ExportActiveSessionToDisk("json");
        Assert.True(File.Exists(jsonFilePath), $"Exported JSON file '{jsonFilePath}' should exist on disk");
        string jsonContent = File.ReadAllText(jsonFilePath);
        Assert.Contains(session.Id, jsonContent);

        // Export Markdown to disk
        string mdFilePath = state.ExportActiveSessionToDisk("md");
        Assert.True(File.Exists(mdFilePath), $"Exported Markdown file '{mdFilePath}' should exist on disk");
        string mdContent = File.ReadAllText(mdFilePath);
        Assert.Contains(session.Title, mdContent);

        state.DeleteSession(session.Id);
    }
}
