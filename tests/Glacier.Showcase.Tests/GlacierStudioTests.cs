using System;
using System.IO;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

[Collection("GlacierStudioSessionTests")]
public class GlacierStudioTests
{
    [Fact]
    public void StudioState_InitializesWithDefaultSessionAndCorrectSnapshot()
    {
        var state = new GlacierStudioState();

        Assert.NotEmpty(state.Sessions);
        Assert.NotNull(state.ActiveSession);
        Assert.Equal(ModelServerProvider.LocalOllama, state.Provider);
        Assert.Contains("Ollama", state.ActiveSession.ModelSnapshot);
    }

    [Fact]
    public void StudioState_SwitchProvider_UpdatesSnapshotAndEndpoints()
    {
        var state = new GlacierStudioState();

        state.SetProvider(ModelServerProvider.LocalGlacierInference);
        Assert.Equal(ModelServerProvider.LocalGlacierInference, state.Provider);
        Assert.Contains("Glacier.Inference", state.ActiveSession.ModelSnapshot);

        state.SetProvider(ModelServerProvider.RemoteGemini);
        Assert.Equal(ModelServerProvider.RemoteGemini, state.Provider);
        Assert.Contains("Gemini", state.ActiveSession.ModelSnapshot);
    }

    [Fact]
    public void StudioState_SessionManagement_CreatesAndSwitchesCorrectly()
    {
        var state = new GlacierStudioState();
        int initialCount = state.Sessions.Count;

        var newSession = state.CreateNewSession("Custom Engineering Session");
        Assert.Equal(initialCount + 1, state.Sessions.Count);
        Assert.Equal(newSession.Id, state.ActiveSession.Id);
        Assert.Equal("Custom Engineering Session", state.ActiveSession.Title);

        state.DeleteSession(newSession.Id);
        Assert.Equal(initialCount, state.Sessions.Count);
    }

    [Fact]
    public void DocTreePreviewService_ParsesMarkdownAndBuildsAstOutline()
    {
        var service = new GlacierDocTreePreviewService();
        string sampleMd = @"# ❄️ Glacier Engine
Welcome to the suite.

## Architecture Overview
Here is how it is structured.

### SIMD Acceleration
AVX-512 kernels.

## Performance Benchmarks
Sub-millisecond results.";

        service.SetDocument("TestDoc.md", sampleMd, "TestDoc.md");

        var doc = service.ActiveDocument;
        Assert.NotNull(doc);
        Assert.Equal("TestDoc.md", doc.Title);
        Assert.True(doc.WordCount > 0);
        Assert.True(doc.Outline.Count >= 3);

        // Verify outline levels
        Assert.Equal("❄️ Glacier Engine", doc.Outline[0].Title);
        Assert.Equal(1, doc.Outline[0].Level);
        Assert.Equal("Architecture Overview", doc.Outline[1].Title);
        Assert.Equal(2, doc.Outline[1].Level);
        Assert.Equal("SIMD Acceleration", doc.Outline[2].Title);
        Assert.Equal(3, doc.Outline[2].Level);
    }

    [Fact]
    public async Task ConsoleService_ExecutesCommandAndCapturesOutput()
    {
        var service = new GlacierConsoleService();
        var (exitCode, output) = await service.ExecuteCommandAsync("echo 'Glacier Studio Test'");

        Assert.Equal(0, exitCode);
        Assert.Contains("Glacier Studio Test", output);
        Assert.NotEmpty(service.Logs);
    }

    [Fact]
    public void McpManager_DiscoversServersOrProvidesDefaults()
    {
        var manager = new GlacierMcpManager();
        Assert.NotEmpty(manager.Servers);

        // Should have at least one server
        var server = manager.Servers[0];
        Assert.False(string.IsNullOrEmpty(server.Name));
        Assert.NotEmpty(server.AvailableTools);
    }
}
