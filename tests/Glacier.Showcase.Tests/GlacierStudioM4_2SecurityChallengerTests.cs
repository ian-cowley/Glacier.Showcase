using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Dedicated Empirical Security Challenger Tests for Milestone M4_2.
/// Stress-tests the Zero Secrets Policy, in-memory credential isolation,
/// session disk persistence, JSON/Markdown exports, and scanner regex conformance.
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierStudioM4_2SecurityChallengerTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;
    private readonly string _exportsDir;
    private const string MockApiKey = "dummy_test_token_isolated_never_persist_123";

    public GlacierStudioM4_2SecurityChallengerTests()
    {
        _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        _sessionsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "sessions");
        _exportsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "exports");
    }

    public void Dispose()
    {
        CleanupTestFiles();
    }

    private void CleanupTestFiles()
    {
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

        if (Directory.Exists(_exportsDir))
        {
            foreach (var file in Directory.GetFiles(_exportsDir, "*challenger_sec_*.*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    [Fact]
    public void SecurityChallenger_ApiKeyIsolation_UnderAllProvidersAndMutations()
    {
        var state = new GlacierStudioState();
        state.ApiKey = MockApiKey;

        var providers = Enum.GetValues<ModelServerProvider>();
        foreach (var provider in providers)
        {
            state.SetProvider(provider);

            // API key must remain in memory
            Assert.Equal(MockApiKey, state.ApiKey);

            // API key must NOT be leaked into model snapshot, display name, or active session metadata
            Assert.DoesNotContain(MockApiKey, state.ActiveSession.ModelSnapshot);
            Assert.DoesNotContain(MockApiKey, state.GetModelDisplayName());
            Assert.DoesNotContain(MockApiKey, state.ActiveSession.Title);
            Assert.DoesNotContain(MockApiKey, state.ActiveSession.Id);
            Assert.DoesNotContain(MockApiKey, state.EndpointUrl);
        }
    }

    [Fact]
    public void SecurityChallenger_SessionSaveToDisk_NeverSerializesApiKey()
    {
        CleanupTestFiles();
        var state = new GlacierStudioState();
        state.ApiKey = MockApiKey;

        string sessionId = "challenger_sec_save_" + Guid.NewGuid().ToString("N")[..6];
        var session = state.CreateNewSession(sessionId);

        // Populate realistic multi-turn conversation
        session.Messages.Add(new ChatMessage
        {
            Role = "system",
            Content = "You are a secure assistant operating under Zero Secrets Policy."
        });

        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Review the security architecture of Glacier Studio."
        });

        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = "All credentials remain strictly in ephemeral process memory.",
            ThinkingContent = "Verifying that no credential strings leak to disk storage."
        };

        assistantMsg.ToolCalls.Add(new AgentToolCallRecord
        {
            ToolName = "security_audit_tool",
            ArgumentsJson = "{\"scope\":\"disk_persistence\",\"isolate_keys\":true}",
            ResultOutput = "Status: PASSED. Zero credentials written to disk.",
            Duration = TimeSpan.FromMilliseconds(42),
            IsSuccess = true
        });

        session.Messages.Add(assistantMsg);

        state.SaveActiveSession();

        string sessionFilePath = Path.Combine(_sessionsDir, $"{session.Id}.json");
        Assert.True(File.Exists(sessionFilePath), $"Expected session file at '{sessionFilePath}'");

        string rawDiskContent = File.ReadAllText(sessionFilePath);

        // Assert secret is NEVER in disk file
        Assert.DoesNotContain(MockApiKey, rawDiskContent);

        // Parse JSON and verify no ApiKey property was serialized
        using var doc = JsonDocument.Parse(rawDiskContent);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("ApiKey", out _), "Session JSON root must NOT contain 'ApiKey' property.");
        Assert.False(root.TryGetProperty("apiKey", out _), "Session JSON root must NOT contain 'apiKey' property.");
        Assert.False(root.TryGetProperty("Key", out _), "Session JSON root must NOT contain 'Key' property.");
        Assert.False(root.TryGetProperty("Secret", out _), "Session JSON root must NOT contain 'Secret' property.");
        Assert.False(root.TryGetProperty("Token", out _), "Session JSON root must NOT contain 'Token' property.");

        // Clean up
        state.DeleteSession(session.Id);
    }

    [Fact]
    public void SecurityChallenger_SessionExportJsonAndMarkdown_NeverContainsApiKey()
    {
        CleanupTestFiles();
        var state = new GlacierStudioState();
        state.ApiKey = MockApiKey;

        string title = "challenger_sec_export_" + Guid.NewGuid().ToString("N")[..6];
        var session = state.CreateNewSession(title);

        session.Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = "Export test message."
        });

        session.Messages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = "Export response content.",
            ThinkingContent = "Deep thought process with zero secrets."
        });

        // Test ExportActiveSessionAsJson
        string jsonExport = state.ExportActiveSessionAsJson();
        Assert.DoesNotContain(MockApiKey, jsonExport);

        using (var doc = JsonDocument.Parse(jsonExport))
        {
            Assert.False(doc.RootElement.TryGetProperty("ApiKey", out _));
            Assert.False(doc.RootElement.TryGetProperty("apiKey", out _));
        }

        // Test ExportActiveSessionAsMarkdown
        string mdExport = state.ExportActiveSessionAsMarkdown();
        Assert.DoesNotContain(MockApiKey, mdExport);

        // Test ExportActiveSessionToDisk (JSON & MD)
        string diskJsonPath = state.ExportActiveSessionToDisk("json");
        Assert.True(File.Exists(diskJsonPath));
        string diskJsonContent = File.ReadAllText(diskJsonPath);
        Assert.DoesNotContain(MockApiKey, diskJsonContent);

        string diskMdPath = state.ExportActiveSessionToDisk("md");
        Assert.True(File.Exists(diskMdPath));
        string diskMdContent = File.ReadAllText(diskMdPath);
        Assert.DoesNotContain(MockApiKey, diskMdContent);

        // Clean up
        state.DeleteSession(session.Id);
        try { File.Delete(diskJsonPath); } catch { }
        try { File.Delete(diskMdPath); } catch { }
    }

    [Fact]
    public void SecurityChallenger_AppsettingsFiles_ContainZeroHardcodedSecrets()
    {
        var showcaseDir = File.Exists(Path.Combine(_workspaceRoot, "appsettings.json"))
            ? _workspaceRoot
            : Path.Combine(_workspaceRoot, "Glacier.Showcase");
        var appsettingsPath = Path.Combine(showcaseDir, "appsettings.json");
        var appsettingsDevPath = Path.Combine(showcaseDir, "appsettings.Development.json");
        var appsettingsTplPath = Path.Combine(showcaseDir, "appsettings.template.json");

        Assert.True(File.Exists(appsettingsPath), $"appsettings.json must exist at '{appsettingsPath}'.");
        Assert.True(File.Exists(appsettingsDevPath), $"appsettings.Development.json must exist at '{appsettingsDevPath}'.");
        Assert.True(File.Exists(appsettingsTplPath), $"appsettings.template.json must exist at '{appsettingsTplPath}'.");

        // Check appsettings.json
        string jsonMain = File.ReadAllText(appsettingsPath);
        using (var doc = JsonDocument.Parse(jsonMain))
        {
            if (doc.RootElement.TryGetProperty("GeminiApiKey", out var keyProp))
            {
                Assert.True(string.IsNullOrEmpty(keyProp.GetString()), "GeminiApiKey in appsettings.json must be empty string.");
            }
        }

        // Check appsettings.template.json
        string jsonTpl = File.ReadAllText(appsettingsTplPath);
        using (var doc = JsonDocument.Parse(jsonTpl))
        {
            if (doc.RootElement.TryGetProperty("GeminiApiKey", out var keyProp))
            {
                Assert.True(string.IsNullOrEmpty(keyProp.GetString()), "GeminiApiKey in appsettings.template.json must be empty string.");
            }
        }
    }

    [Fact]
    public void SecurityChallenger_ScannerRegexRules_ConfirmSessionOutputPassesAllRules()
    {
        // Replicate high-confidence regex patterns from scripts/security_check.py
        var secretPatterns = new[]
        {
            new Regex(@"\bAIza[0-9A-Za-z-_]{35}\b", RegexOptions.Compiled),
            new Regex(@"\bsk-[a-zA-Z0-9]{20,}\b", RegexOptions.Compiled),
            new Regex(@"\bsk-ant-[a-zA-Z0-9-_]{20,}\b", RegexOptions.Compiled),
            new Regex(@"\bgh[pousr]_[A-Za-z0-9_]{36,255}\b", RegexOptions.Compiled),
            new Regex(@"\b(AKIA|ABIA|ACCA|ASIA)[0-9A-Z]{16}\b", RegexOptions.Compiled),
            new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled),
            new Regex(@"(?i)(?:api[_-]?key|secret[_-]?key|access[_-]?token|auth[_-]?token|private[_-]?key|password|client[_-]?secret)\s*[:=]\s*[""']([^""'\s]{12,})[""']", RegexOptions.Compiled)
        };

        var state = new GlacierStudioState();
        state.ApiKey = MockApiKey;

        string title = "challenger_sec_regex_" + Guid.NewGuid().ToString("N")[..6];
        var session = state.CreateNewSession(title);
        session.Messages.Add(new ChatMessage { Role = "user", Content = "Checking regex conformity." });
        session.Messages.Add(new ChatMessage { Role = "assistant", Content = "Conformity verified." });

        string json = state.ExportActiveSessionAsJson();
        string md = state.ExportActiveSessionAsMarkdown();

        foreach (var pattern in secretPatterns)
        {
            var matchJson = pattern.Match(json);
            Assert.False(matchJson.Success, $"Session JSON export matched secret pattern '{pattern}': '{matchJson.Value}'");

            var matchMd = pattern.Match(md);
            Assert.False(matchMd.Success, $"Session Markdown export matched secret pattern '{pattern}': '{matchMd.Value}'");
        }

        state.DeleteSession(session.Id);
    }
}
