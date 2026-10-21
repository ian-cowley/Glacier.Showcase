using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Glacier.Showcase.Services.GlacierStudio;
using Xunit;

namespace Glacier.Showcase.Tests;

/// <summary>
/// Empirical verification and stress test suite by Challenger 2 for Milestone M3:
/// Hardware-Aware Model Selection, VRAM Bounds (4.8 GB), and Server Launch Guards.
/// </summary>
public class ChallengerM3EmpiricalTests
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

    private static GlacierRuntimeLauncherService CreateLauncher(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? httpHandler = null)
    {
        var handler = new MockHttpHandler(httpHandler ?? ((req, ct) =>
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));

        var client = new HttpClient(handler);
        var state = new GlacierStudioState();
        var console = new GlacierConsoleService();

        return new GlacierRuntimeLauncherService(
            state,
            console,
            workspaceRoot: null,
            probeClient: client,
            startBackgroundProber: false,
            autoDiscover: false,
            autoProbe: false);
    }

    #region 1. VRAM Threshold Boundaries (4.8 GB Usable Limit)

    [Fact]
    public void VramBoundary_ExactThreshold_4Point8000Gb_IsPermitted()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Exact-Boundary-Model", @"D:\models\exact.gguf", 4.800000);

        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);
        string bestPath = launcher.GetBestFittingLocalModel(4.8);

        Assert.NotNull(bestInfo);
        Assert.Equal(4.8, bestInfo.SizeGb, 5);
        Assert.Equal(@"D:\models\exact.gguf", bestPath);
    }

    [Fact]
    public void VramBoundary_SlightlyAboveThreshold_4Point8001Gb_IsStrictlyExcluded()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Slightly-Over-Model", @"D:\models\over.gguf", 4.8001);

        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);
        string bestPath = launcher.GetBestFittingLocalModel(4.8);

        Assert.Null(bestInfo);
        Assert.Equal("", bestPath);
    }

    [Fact]
    public void VramBoundary_14BModel_8Point8Gb_StrictlyExcludedFromStandalone()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Glacier-Architect-Coder-14B-Grandmaster (8.8 GB)", @"D:\models\14b-grandmaster.gguf", 8.83);
        launcher.AddDiscoveredModel("DeepSeek-R1-Distill-Qwen-14B (9.0 GB)", @"D:\models\14b-r1.gguf", 8.98);

        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);
        string bestPath = launcher.GetBestFittingLocalModel(4.8);

        Assert.Null(bestInfo);
        Assert.Equal("", bestPath);
    }

    [Fact]
    public async Task StartInferenceServerAsync_14BModel_RejectsLaunch_WhenNoFittingModelExists()
    {
        string temp14BFile = Path.Combine(Path.GetTempPath(), $"test_14b_{Guid.NewGuid():N}.gguf");
        try
        {
            // Create a sparse dummy file of 8.83 GB
            using (var fs = new FileStream(temp14BFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength((long)(8.83 * 1024 * 1024 * 1024));
            }

            using var launcher = CreateLauncher();
            // Only add the 14B model (exceeding budget)
            launcher.AddDiscoveredModel("Glacier-Architect-Coder-14B-Grandmaster (8.8 GB)", temp14BFile, 8.83);
            launcher.SelectedGgufPath = temp14BFile;

            bool result = await launcher.StartInferenceServerAsync();

            Assert.False(result);
            Assert.Equal(RuntimeHealthState.Offline, launcher.InferenceState);
            Assert.Contains("exceeds 4.8GB budget and no local 4B/7B model found", launcher.InferenceStatusDetail);
        }
        finally
        {
            if (File.Exists(temp14BFile)) File.Delete(temp14BFile);
        }
    }

    [Fact]
    public async Task StartInferenceServerAsync_14BModel_AutoPairsWithFittingModel_WhenFittingModelExists()
    {
        string temp14BFile = Path.Combine(Path.GetTempPath(), $"test_14b_{Guid.NewGuid():N}.gguf");
        string temp7BFile = Path.Combine(Path.GetTempPath(), $"test_coder7b_{Guid.NewGuid():N}.gguf");
        try
        {
            using (var fs = new FileStream(temp14BFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength((long)(8.83 * 1024 * 1024 * 1024));
            }
            using (var fs = new FileStream(temp7BFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength((long)(4.68 * 1024 * 1024 * 1024));
            }

            using var launcher = CreateLauncher();
            launcher.AddDiscoveredModel("Glacier-Architect-Coder-14B-Grandmaster (8.8 GB)", temp14BFile, 8.83);
            launcher.AddDiscoveredModel("qwen2.5-coder-7b (4.7 GB)", temp7BFile, 4.68);

            // Request the 14B model for single-node standalone launch
            launcher.SelectedGgufPath = temp14BFile;

            // StartInferenceServerAsync should detect that 14B exceeds 4.8 GB and redirect SelectedGgufPath to the 7B model!
            _ = await launcher.StartInferenceServerAsync();

            // SelectedGgufPath MUST have been changed away from 14B to the 7B fitting model
            Assert.Equal(temp7BFile, launcher.SelectedGgufPath);
            Assert.NotEqual(temp14BFile, launcher.SelectedGgufPath);
        }
        finally
        {
            if (File.Exists(temp14BFile)) File.Delete(temp14BFile);
            if (File.Exists(temp7BFile)) File.Delete(temp7BFile);
        }
    }

    #endregion

    #region 2. Ranking Priority Hierarchy (Coder-7B -> Qwen3-4B -> Smallest Fitting)

    [Fact]
    public void RankingPriority_Coder7B_TakesPrecedenceOver_Qwen3_4B_And_Others()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Generic-Tiny (0.5 GB)", @"D:\tiny.gguf", 0.5);
        launcher.AddDiscoveredModel("Qwen3-4B-Instruct (2.5 GB)", @"D:\qwen3_4b.gguf", 2.49);
        launcher.AddDiscoveredModel("qwen2.5-coder-7b (4.7 GB)", @"D:\coder7b.gguf", 4.68);
        launcher.AddDiscoveredModel("Qwen2.5-7B-Instruct (4.5 GB)", @"D:\qwen7b.gguf", 4.5);

        var best = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.NotNull(best);
        Assert.Equal(@"D:\coder7b.gguf", best.FullPath);
        Assert.Contains("coder-7b", best.DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RankingPriority_Qwen3_4B_TakesPrecedenceWhenCoder7BIsAbsent()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Generic-Tiny (0.5 GB)", @"D:\tiny.gguf", 0.5);
        launcher.AddDiscoveredModel("Qwen3-4B-Instruct (2.5 GB)", @"D:\qwen3_4b_inst.gguf", 2.49);
        launcher.AddDiscoveredModel("Qwen2.5-7B-Instruct (4.5 GB)", @"D:\qwen7b.gguf", 4.5);

        var best = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.NotNull(best);
        Assert.Equal(@"D:\qwen3_4b_inst.gguf", best.FullPath);
    }

    [Fact]
    public void RankingPriority_SmallestFittingModel_ChosenWhenNamedModelsAreAbsent()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Llama-3.2-3B (3.2 GB)", @"D:\llama32.gguf", 3.2);
        launcher.AddDiscoveredModel("Custom-Medium (2.1 GB)", @"D:\custom_med.gguf", 2.1);
        launcher.AddDiscoveredModel("SmolLM-360M (0.4 GB)", @"D:\smollm.gguf", 0.4);
        launcher.AddDiscoveredModel("TinyLlama-1.1B (0.9 GB)", @"D:\tinyllama.gguf", 0.9);

        var best = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.NotNull(best);
        // Smallest fitting model should be chosen
        Assert.Equal(@"D:\smollm.gguf", best.FullPath);
        Assert.Equal(0.4, best.SizeGb);
    }

    [Fact]
    public void RankingPriority_Qwen3_4B_Instruct_FavoredOver_Qwen3_4B_Plain()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Qwen3-4B (2.5 GB)", @"D:\qwen3_plain.gguf", 2.49);
        launcher.AddDiscoveredModel("Qwen3-4B-Instruct (2.5 GB)", @"D:\qwen3_instruct.gguf", 2.49);

        var best = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.NotNull(best);
        Assert.Equal(@"D:\qwen3_instruct.gguf", best.FullPath);
    }

    #endregion

    #region 3. Zero Fitting Models Edge Cases & Null Safety

    [Fact]
    public void ZeroFittingModels_EmptyCatalog_ReturnsEmptyStringAndNullWithoutThrowing()
    {
        using var launcher = CreateLauncher();

        string bestPath = launcher.GetBestFittingLocalModel(4.8);
        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.Equal("", bestPath);
        Assert.Null(bestInfo);

        // Turn resolution should use safe fallbacks without throwing NullReferenceException
        string localTurnModel = launcher.ResolveModelForTurn(AgentTurnTier.LocalFastTool);
        Assert.Equal("qwen2.5-coder-7b", localTurnModel);

        launcher.SetCircuitStateForTesting(CircuitBreakerState.Open);
        string reasoningTurnModel = launcher.ResolveModelForTurn(AgentTurnTier.RemoteDeepReasoning);
        Assert.Equal("qwen2.5-coder-7b", reasoningTurnModel);
    }

    [Fact]
    public void ZeroFittingModels_AllExceedBudget_ReturnsEmptyStringAndNullWithoutThrowing()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Qwen-14B (8.8 GB)", @"D:\14b.gguf", 8.8);
        launcher.AddDiscoveredModel("Qwen-32B (19.5 GB)", @"D:\32b.gguf", 19.5);
        launcher.AddDiscoveredModel("Llama-70B (40.0 GB)", @"D:\70b.gguf", 40.0);

        string bestPath = launcher.GetBestFittingLocalModel(4.8);
        var bestInfo = launcher.GetBestFittingLocalModelInfo(4.8);

        Assert.Equal("", bestPath);
        Assert.Null(bestInfo);

        string localTurnModel = launcher.ResolveModelForTurn(AgentTurnTier.LocalFastTool);
        Assert.Equal("qwen2.5-coder-7b", localTurnModel);
    }

    [Fact]
    public async Task ZeroFittingModels_StartInference_EmptySelectedPath_ReturnsFalse()
    {
        using var launcher = CreateLauncher();
        launcher.SelectedGgufPath = "";

        bool started = await launcher.StartInferenceServerAsync();

        Assert.False(started);
        Assert.Contains("Select a valid .gguf model file first", launcher.InferenceStatusDetail);
    }

    #endregion

    #region 4. Extreme VRAM Budgets & Boundary Inputs

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    public void ExtremeBudgets_ZeroOrNegativeBudget_YieldsNoModels(double budget)
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("SmolLM-360M (0.4 GB)", @"D:\smollm.gguf", 0.4);

        var bestInfo = launcher.GetBestFittingLocalModelInfo(budget);
        string bestPath = launcher.GetBestFittingLocalModel(budget);

        Assert.Null(bestInfo);
        Assert.Equal("", bestPath);
    }

    [Fact]
    public void ExtremeBudgets_HighBudget_Allows14BModelsIfConfigured()
    {
        using var launcher = CreateLauncher();
        launcher.AddDiscoveredModel("Glacier-Architect-Coder-14B-Grandmaster (8.8 GB)", @"D:\14b.gguf", 8.83);

        // On a high VRAM machine with 24 GB VRAM budget
        var bestInfo = launcher.GetBestFittingLocalModelInfo(24.0);

        Assert.NotNull(bestInfo);
        Assert.Equal(8.83, bestInfo.SizeGb);
        Assert.Equal(@"D:\14b.gguf", bestInfo.FullPath);
    }

    #endregion
}
