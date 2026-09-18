namespace Glacier.Showcase.Tests;

using System;
using System.IO;
using System.Linq;
using Glacier.Showcase.Services;
using Glacier.Tune.Trainer;
using Xunit;

public class MissionControlServiceTests : IDisposable
{
    private readonly MissionControlService _service;

    public MissionControlServiceTests()
    {
        _service = new MissionControlService();
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    [Fact]
    public void KnowledgeGraph_Initialization_ContainsCoreEcosystemNodesAndEdges()
    {
        var (nodes, edges) = _service.GetGraphData();

        Assert.NotNull(nodes);
        Assert.NotNull(edges);
        Assert.True(nodes.Count >= 10, "Knowledge graph should contain core nodes.");
        Assert.True(edges.Count >= 10, "Knowledge graph should contain core edges.");

        var nodeLabels = nodes.Select(n => n.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Glacier.Inference", nodeLabels);
        Assert.Contains("Glacier.Tune", nodeLabels);
        Assert.Contains("Glacier.Serve", nodeLabels);
        Assert.Contains("Glacier.Rag", nodeLabels);
        Assert.Contains("PagedAttention", nodeLabels);

        // Verify categories are correctly assigned
        var engineNode = nodes.First(n => n.Label == "Glacier.Inference");
        Assert.Equal("ENGINE", engineNode.Category);

        var modelNode = nodes.First(n => n.Label == "Qwen-2.5");
        Assert.Equal("MODEL", modelNode.Category);

        var featureNode = nodes.First(n => n.Label == "PagedAttention");
        Assert.Equal("FEATURE", featureNode.Category);
    }

    [Fact]
    public void KnowledgeGraph_GetNeighborhood_ReturnsExpectedConnections()
    {
        var neighbors = _service.GetNeighborhood("Glacier.Inference", hops: 1);

        Assert.NotNull(neighbors);
        Assert.NotEmpty(neighbors);
        Assert.Contains("Llama-3.1", neighbors);
        Assert.Contains("Qwen-2.5", neighbors);
    }

    [Fact]
    public void PagedAttention_Tracking_ReturnsAccurateBlockPoolStats()
    {
        var blocks = _service.GetPagedBlocks();
        Assert.Equal(64, blocks.Count);

        var stats = _service.GetPagedPoolStats();
        Assert.True(stats.AllocatedBlocks > 0, "Allocated blocks should be greater than zero due to seeding.");
        Assert.True(stats.FreeBlocks > 0, "Free blocks should be greater than zero.");
        Assert.Equal(64, stats.AllocatedBlocks + stats.FreeBlocks);
        Assert.Equal(64 * 16, stats.CapacityTokens);
        Assert.InRange(stats.MemoryReductionPct, 0.0, 95.0);
    }

    [Fact]
    public void TrainingHistory_EmpiricalTelemetry_ContainsAuthenticGlacierTuneLogs()
    {
        var history = _service.TrainingHistory;

        Assert.NotNull(history);
        Assert.Equal(10, history.Count);

        // Verify Step 1: loss 21.8704, lr 2.00e-4, 46.0 tok/sec
        var step1 = history[0];
        Assert.Equal(1, step1.Step);
        Assert.Equal(21.8704f, step1.Loss, precision: 3);
        Assert.Equal(2.00e-4f, step1.LearningRate, precision: 6);
        Assert.Equal(46.0, step1.TokensPerSec, precision: 1);

        // Verify Step 10: loss 10.9248, lr 1.00e-4, 46.0 tok/sec
        var step10 = history[9];
        Assert.Equal(10, step10.Step);
        Assert.Equal(10.9248f, step10.Loss, precision: 3);
        Assert.Equal(1.00e-4f, step10.LearningRate, precision: 6);

        // Verify loss monotonically decreases overall (authentic convergence curve)
        Assert.True(step1.Loss > step10.Loss, "Training loss should decrease over 10 empirical steps.");

        // Check each step for non-zero valid numbers
        for (int i = 0; i < history.Count; i++)
        {
            Assert.Equal(i + 1, history[i].Step);
            Assert.True(history[i].Loss > 0.0f, $"Step {i + 1} loss must be positive.");
            Assert.True(history[i].LearningRate > 0.0f, $"Step {i + 1} learning rate must be positive.");
            Assert.True(history[i].TokensPerSec > 30.0, $"Step {i + 1} throughput must reflect physical hardware.");
        }
    }

    [Fact]
    public void StreamTelemetryStep_AppendsLiveTrainingStepResult()
    {
        int initialCount = _service.TrainingHistory.Count;
        var stepResult = new TrainingStepResult(11, 10.1234f, 6500.0, 48.2);

        _service.StreamTelemetryStep(stepResult, lr: 0.95e-4f);

        Assert.Equal(initialCount + 1, _service.TrainingHistory.Count);
        var lastPoint = _service.TrainingHistory.Last();
        Assert.Equal(11, lastPoint.Step);
        Assert.Equal(10.1234f, lastPoint.Loss);
        Assert.Equal(0.95e-4f, lastPoint.LearningRate);
        Assert.Equal(48.2, lastPoint.TokensPerSec);
    }
}
