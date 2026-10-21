using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Dedicated Unit Tests for GlacierStudioState.
/// Verifies provider presets, session lifecycle, in-memory credential protection,
/// search queries, and export formats.
/// </summary>
[Collection("GlacierStudioSessionTests")]
public class GlacierStudioStateTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly string _sessionsDir;

    public GlacierStudioStateTests()
    {
        _workspaceRoot = GlacierWorkspaceResolver.ResolveWorkspaceRoot();
        _sessionsDir = Path.Combine(_workspaceRoot, ".glacier", "studio", "sessions");
    }

    public void Dispose()
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
    }

    [Fact]
    public void StudioState_InitialState_HasExpectedDefaults()
    {
        var state = new GlacierStudioState();

        Assert.NotEmpty(state.Sessions);
        Assert.NotNull(state.ActiveSession);
        Assert.Equal(ModelServerProvider.LocalOllama, state.Provider);
        Assert.Equal("llama3.2", state.SelectedModel);
        Assert.Equal(AgentExecutionStatus.Idle, state.ExecutionStatus);
        Assert.Equal(0.0, state.LastTokensPerSecond);
        Assert.Equal(TimeSpan.Zero, state.LastTurnDuration);
        Assert.True(state.IsAgentMode);
        Assert.Equal("doc", state.ActiveRightTab);
    }

    [Fact]
    public void StudioState_GetModelPresetsForProvider_ReturnsNonEmptyPresetsForAllProviders()
    {
        var state = new GlacierStudioState();
        var providers = Enum.GetValues<ModelServerProvider>();

        foreach (var p in providers)
        {
            var presets = state.GetModelPresetsForProvider(p);
            Assert.NotNull(presets);
            Assert.NotEmpty(presets);
            Assert.All(presets, preset => Assert.False(string.IsNullOrWhiteSpace(preset)));
        }
    }

    [Fact]
    public void StudioState_SetProvider_UpdatesEndpointAndSnapshot()
    {
        var state = new GlacierStudioState();

        state.SetProvider(ModelServerProvider.LocalGlacierInference);
        Assert.Equal(ModelServerProvider.LocalGlacierInference, state.Provider);
        Assert.Contains("Glacier.Inference", state.ActiveSession.ModelSnapshot);
        Assert.Contains("11434", state.EndpointUrl);

        state.SetProvider(ModelServerProvider.LocalLMStudio);
        Assert.Equal(ModelServerProvider.LocalLMStudio, state.Provider);
        Assert.Contains("LM Studio", state.ActiveSession.ModelSnapshot);
        Assert.Contains("1234", state.EndpointUrl);

        state.SetProvider(ModelServerProvider.RemoteOpenAI);
        Assert.Equal(ModelServerProvider.RemoteOpenAI, state.Provider);
        Assert.Contains("OpenAI", state.ActiveSession.ModelSnapshot);
        Assert.Contains("api.openai.com", state.EndpointUrl);
    }

    [Fact]
    public void StudioState_ApiKey_NeverSerializedToSessionJson()
    {
        var state = new GlacierStudioState();
        string dummyInMemoryToken = "dummy_test_token_isolated_never_persist_123";
        state.ApiKey = dummyInMemoryToken;

        var session = state.CreateNewSession("unit_test_SecretHygiene");
        state.SaveActiveSession();

        string jsonExport = state.ExportActiveSessionAsJson();
        Assert.DoesNotContain(dummyInMemoryToken, jsonExport);

        string mdExport = state.ExportActiveSessionAsMarkdown();
        Assert.DoesNotContain(dummyInMemoryToken, mdExport);

        string sessionFilePath = Path.Combine(_sessionsDir, $"{session.Id}.json");
        if (File.Exists(sessionFilePath))
        {
            string diskContent = File.ReadAllText(sessionFilePath);
            Assert.DoesNotContain(dummyInMemoryToken, diskContent);
        }

        state.DeleteSession(session.Id);
    }

    [Fact]
    public void StudioState_NotifyStateChanged_InvokesSubscribedHandler()
    {
        var state = new GlacierStudioState();
        bool notified = false;

        state.OnStateChanged += () => notified = true;
        state.NotifyStateChanged();

        Assert.True(notified, "OnStateChanged must fire when NotifyStateChanged is called.");
    }

    [Fact]
    public void StudioState_FilteredSessions_CorrectlyFiltersByTitleAndContent()
    {
        var state = new GlacierStudioState();
        string titleA = "unit_test_Alpha_" + Guid.NewGuid().ToString("N")[..6];
        string titleB = "unit_test_Beta_" + Guid.NewGuid().ToString("N")[..6];

        var sessionA = state.CreateNewSession(titleA);
        sessionA.Messages.Add(new ChatMessage { Role = "user", Content = "Alpha specific query topic" });

        var sessionB = state.CreateNewSession(titleB);
        sessionB.Messages.Add(new ChatMessage { Role = "user", Content = "Beta specific query topic" });

        // Search for Alpha
        state.SessionSearchQuery = "Alpha";
        var filteredAlpha = state.FilteredSessions.ToList();
        Assert.Contains(filteredAlpha, s => s.Id == sessionA.Id);
        Assert.DoesNotContain(filteredAlpha, s => s.Id == sessionB.Id);

        // Search for Beta
        state.SessionSearchQuery = "Beta";
        var filteredBeta = state.FilteredSessions.ToList();
        Assert.Contains(filteredBeta, s => s.Id == sessionB.Id);
        Assert.DoesNotContain(filteredBeta, s => s.Id == sessionA.Id);

        // Clean up
        state.DeleteSession(sessionA.Id);
        state.DeleteSession(sessionB.Id);
    }
}
