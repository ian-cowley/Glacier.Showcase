namespace Glacier.Showcase.Tests;

using System;
using System.Linq;
using System.Threading.Tasks;
using Glacier.Showcase.Services;
using Xunit;

public class GpuAccelerationBenchmarkServiceTests
{
    private readonly GpuAccelerationBenchmarkService _service = new();

    [Fact]
    public void DetectedHardware_ReturnsDynamicHardwareDescription()
    {
        string hardware = _service.DetectedHardware;

        Assert.False(string.IsNullOrWhiteSpace(hardware));

        // It should identify either a real GPU device (NVIDIA/AMD/Intel) or Host CPU with SIMD capability
        bool hasGpuOrCpuSimd = hardware.Contains("VRAM", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("SIMD", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("AVX", StringComparison.OrdinalIgnoreCase) ||
                               hardware.Contains("CPU", StringComparison.OrdinalIgnoreCase);

        Assert.True(hasGpuOrCpuSimd, $"DetectedHardware '{hardware}' should report authentic hardware or SIMD capabilities.");
    }

    [Fact]
    public async Task RunAllBenchmarksAsync_ExecutesAllSixPillarsWithVerification()
    {
        var results = await _service.RunAllBenchmarksAsync();

        Assert.NotNull(results);
        Assert.Equal(6, results.Count);

        string[] expectedPillars =
        [
            "Glacier.Polaris",
            "Glacier.ML",
            "Glacier.Tensor",
            "Glacier.Vector",
            "Glacier.Plot",
            "Glacier.StatsViz"
        ];

        for (int i = 0; i < expectedPillars.Length; i++)
        {
            var r = results[i];
            Assert.Equal(expectedPillars[i], r.Pillar);
            Assert.False(string.IsNullOrWhiteSpace(r.Operation));
            Assert.False(string.IsNullOrWhiteSpace(r.Workload));
            Assert.True(r.CpuTimeMs >= 0, $"{r.Pillar} CPU time must be non-negative.");
            Assert.True(r.GpuTimeMs >= 0, $"{r.Pillar} GPU time must be non-negative.");
            Assert.True(r.Speedup > 0, $"{r.Pillar} speedup must be positive.");
            Assert.False(string.IsNullOrWhiteSpace(r.HardwareUsed));
            Assert.True(r.Verified, $"{r.Pillar} computation must verify mathematical consistency between CPU and accelerator.");
        }
    }
}
