namespace Glacier.Showcase.Services;

using System;
using System.Diagnostics;
using System.Threading.Tasks;

public record BenchmarkResult(
    string Pillar,
    string Operation,
    string Workload,
    double CpuTimeMs,
    double GpuTimeMs,
    double Speedup,
    string MetricName,
    double MetricValue,
    string HardwareUsed,
    bool Verified);

public class GpuAccelerationBenchmarkService
{
    public bool IsGpuAvailable =>
        Glacier.Polaris.Compute.GpuPolarisAccelerator.IsGpuAvailable ||
        Glacier.Tensor.Compute.GpuAccelerator.IsGpuAvailable ||
        Glacier.Vector.Compute.GpuVectorAccelerator.IsGpuAvailable;

    public string DetectedHardware
    {
        get
        {
            try
            {
                var optimalDev = Glacier.Inference.Hardware.DeviceManager.GetOptimalDevice();
                if (optimalDev != null && optimalDev.Vendor != Glacier.Inference.Hardware.GpuVendor.Cpu)
                {
                    string vramMb = $"{optimalDev.DedicatedVramBytes / (1024 * 1024):N0} MB Dedicated VRAM";
                    return $"{optimalDev.Name} ({optimalDev.Vendor}, {vramMb})";
                }

                var adapters = Glacier.Gpu.Drivers.DirectMlDriver.GetAdapters();
                if (adapters.Count > 0)
                {
                    var a = adapters[0];
                    string vramMb = $"{a.DedicatedVramBytes / (1024 * 1024):N0} MB Dedicated VRAM";
                    return $"{a.Description} ({vramMb})";
                }
            }
            catch
            {
                // Fallback to CPU environment description
            }

            string cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Host CPU";
            string simd = System.Runtime.Intrinsics.Vector512.IsHardwareAccelerated ? "AVX-512" :
                          System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated ? "AVX2" : "SIMD";
            return $"{cpuName} ({simd})";
        }
    }

    public async Task<List<BenchmarkResult>> RunAllBenchmarksAsync()
    {
        return await Task.Run(() =>
        {
            var results = new List<BenchmarkResult>();

            // 1. Glacier.Polaris: Vector Math & Sigmoid
            results.Add(BenchmarkPolaris());

            // 2. Glacier.ML: K-Means Cluster Assignment
            results.Add(BenchmarkMlCluster());

            // 3. Glacier.Tensor: Tensor Core MMA Matrix Multiplication
            results.Add(BenchmarkTensorCoreGemm());

            // 4. Glacier.Vector: Batch Vector Scan
            results.Add(BenchmarkVectorBatchScan());

            // 5. Glacier.Plot: MinMax Decimation
            results.Add(BenchmarkPlotMinMax());

            // 6. Glacier.StatsViz: Vectorized Gaussian KDE
            results.Add(BenchmarkStatsVizKde());

            return results;
        });
    }

    private BenchmarkResult BenchmarkPolaris()
    {
        int n = 1_000_000;
        float[] a = new float[n];
        float[] b = new float[n];
        float[] cpuOut = new float[n];
        float[] gpuOut = new float[n];
        Array.Fill(a, 1.5f);
        Array.Fill(b, 2.5f);

        // Warmup
        Glacier.Polaris.Compute.GpuPolarisAccelerator.VectorAdd(a, b, cpuOut, Glacier.Polaris.Compute.GpuTarget.Cpu);
        Glacier.Polaris.Compute.GpuPolarisAccelerator.VectorAdd(a, b, gpuOut, Glacier.Polaris.Compute.GpuTarget.Auto);

        // CPU
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            Glacier.Polaris.Compute.GpuPolarisAccelerator.VectorAdd(a, b, cpuOut, Glacier.Polaris.Compute.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds / 10.0;

        // GPU
        sw.Restart();
        for (int i = 0; i < 10; i++)
            Glacier.Polaris.Compute.GpuPolarisAccelerator.VectorAdd(a, b, gpuOut, Glacier.Polaris.Compute.GpuTarget.Auto);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 10.0;

        bool verified = Math.Abs(cpuOut[0] - gpuOut[0]) < 1e-4f;
        double throughputGb = (n * sizeof(float) * 3.0) / (gpuMs * 1e6); // 2 reads + 1 write

        return new BenchmarkResult(
            "Glacier.Polaris",
            "Vector Column Addition (FP32)",
            "1,000,000 rows",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Throughput (GB/s)",
            throughputGb,
            DetectedHardware,
            verified);
    }

    private BenchmarkResult BenchmarkMlCluster()
    {
        int rows = 50_000;
        int cols = 32;
        int k = 16;
        float[] data = new float[rows * cols];
        var rng = new Random(42);
        for (int i = 0; i < data.Length; i++) data[i] = (float)rng.NextDouble();

        using var features = new Glacier.ML.Core.FeatureMatrix(data, rows, cols);
        var kMeans = new Glacier.ML.Clustering.KMeans(k: k, maxIterations: 5, target: Glacier.ML.Core.GpuTarget.Auto);
        kMeans.Fit(features);

        int[] cpuAssignments = new int[rows];
        int[] gpuAssignments = new int[rows];

        // Warmup
        kMeans.Predict(features, cpuAssignments, Glacier.ML.Core.GpuTarget.Cpu);
        kMeans.Predict(features, gpuAssignments, Glacier.ML.Core.GpuTarget.Auto);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            kMeans.Predict(features, cpuAssignments, Glacier.ML.Core.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        sw.Restart();
        for (int i = 0; i < 5; i++)
            kMeans.Predict(features, gpuAssignments, Glacier.ML.Core.GpuTarget.Auto);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        bool verified = cpuAssignments[0] == gpuAssignments[0];
        double throughputMSamples = (rows / (gpuMs * 1000.0));

        return new BenchmarkResult(
            "Glacier.ML",
            "K-Means Cluster Assignment",
            "50,000 samples x 32 dims (k=16)",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Throughput (M samples/s)",
            throughputMSamples,
            DetectedHardware,
            verified);
    }

    private unsafe BenchmarkResult BenchmarkTensorCoreGemm()
    {
        int m = 1024, k = 1024, n = 1024;
        using var a = new Glacier.Tensor.Core.Tensor<float>(m, k);
        using var b = new Glacier.Tensor.Core.Tensor<float>(k, n);
        using var cpuC = new Glacier.Tensor.Core.Tensor<float>(m, n);
        using var gpuC = new Glacier.Tensor.Core.Tensor<float>(m, n);

        new Span<float>(a.DataPointer, m * k).Fill(0.5f);
        new Span<float>(b.DataPointer, k * n).Fill(0.25f);

        var target = Glacier.Tensor.Compute.GpuAccelerator.HasNvidiaGpu
            ? Glacier.Tensor.Compute.GpuTarget.NvidiaTensorCore
            : Glacier.Tensor.Compute.GpuTarget.Auto;

        // Warmup
        Glacier.Tensor.Compute.GpuAccelerator.AcceleratedMatMul(a, b, cpuC, Glacier.Tensor.Compute.GpuTarget.Cpu);
        Glacier.Tensor.Compute.GpuAccelerator.AcceleratedMatMul(a, b, gpuC, target);

        var sw = Stopwatch.StartNew();
        Glacier.Tensor.Compute.GpuAccelerator.AcceleratedMatMul(a, b, cpuC, Glacier.Tensor.Compute.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        for (int i = 0; i < 5; i++)
            Glacier.Tensor.Compute.GpuAccelerator.AcceleratedMatMul(a, b, gpuC, target);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        bool verified = Math.Abs(cpuC[0, 0] - gpuC[0, 0]) < 1.0f;
        double tflops = (2.0 * m * k * n) / (gpuMs * 1e9);

        return new BenchmarkResult(
            "Glacier.Tensor",
            "Ada Lovelace Tensor Core GEMM",
            "1024 x 1024 FP32 matrix",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Compute (TFLOPS)",
            tflops,
            DetectedHardware,
            verified);
    }

    private BenchmarkResult BenchmarkVectorBatchScan()
    {
        int count = 50_000;
        int dim = 128;
        int batchSize = 32;
        float[] db = new float[count * dim];
        float[] queries = new float[batchSize * dim];
        float[] cpuScores = new float[count * batchSize];
        float[] gpuScores = new float[count * batchSize];
        Array.Fill(queries, 1.0f);
        Array.Fill(db, 0.1f);

        // Warmup
        Glacier.Vector.Compute.GpuVectorAccelerator.BatchScanGpu(db, queries, cpuScores, count, dim, batchSize, Glacier.Vector.Core.GpuTarget.Cpu);
        Glacier.Vector.Compute.GpuVectorAccelerator.BatchScanGpu(db, queries, gpuScores, count, dim, batchSize, Glacier.Vector.Core.GpuTarget.Auto);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            Glacier.Vector.Compute.GpuVectorAccelerator.BatchScanGpu(db, queries, cpuScores, count, dim, batchSize, Glacier.Vector.Core.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        sw.Restart();
        for (int i = 0; i < 5; i++)
            Glacier.Vector.Compute.GpuVectorAccelerator.BatchScanGpu(db, queries, gpuScores, count, dim, batchSize, Glacier.Vector.Core.GpuTarget.Auto);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        bool verified = Math.Abs(cpuScores[0] - gpuScores[0]) < 1e-3f;
        double mVecPerSec = ((double)count * batchSize) / (gpuMs * 1000.0);

        return new BenchmarkResult(
            "Glacier.Vector",
            "Batch Dot-Product Vector Scan",
            "50,000 vectors x 128 dims (batch=32)",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Search Rate (M vec/s)",
            mVecPerSec,
            DetectedHardware,
            verified);
    }

    private BenchmarkResult BenchmarkPlotMinMax()
    {
        int totalPoints = 1_000_000;
        int targetPixelWidth = 2_000;
        int targetPoints = targetPixelWidth * 2;

        float[] x = new float[totalPoints];
        float[] y = new float[totalPoints];
        for (int i = 0; i < totalPoints; i++)
        {
            x[i] = i * 0.1f;
            y[i] = MathF.Sin(i * 0.01f) * 100.0f;
        }

        float[] cpuOutX = new float[targetPoints];
        float[] cpuOutY = new float[targetPoints];
        float[] gpuOutX = new float[targetPoints];
        float[] gpuOutY = new float[targetPoints];

        // Warmup
        Glacier.Plot.Compute.GpuPlotAccelerator.MinMaxDownsample(x, y, targetPixelWidth, cpuOutX, cpuOutY, Glacier.Plot.Core.GpuTarget.Cpu);
        Glacier.Plot.Compute.GpuPlotAccelerator.MinMaxDownsample(x, y, targetPixelWidth, gpuOutX, gpuOutY, Glacier.Plot.Core.GpuTarget.Auto);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
            Glacier.Plot.Compute.GpuPlotAccelerator.MinMaxDownsample(x, y, targetPixelWidth, cpuOutX, cpuOutY, Glacier.Plot.Core.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds / 10.0;

        sw.Restart();
        for (int i = 0; i < 10; i++)
            Glacier.Plot.Compute.GpuPlotAccelerator.MinMaxDownsample(x, y, targetPixelWidth, gpuOutX, gpuOutY, Glacier.Plot.Core.GpuTarget.Auto);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 10.0;

        bool verified = Math.Abs(cpuOutY[0] - gpuOutY[0]) < 1e-4f;
        double throughputMPoints = (totalPoints / (gpuMs * 1000.0));

        return new BenchmarkResult(
            "Glacier.Plot",
            "Min-Max LTTB Decimation",
            "1,000,000 points -> 2,000 pixels",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Decimation (M pts/s)",
            throughputMPoints,
            DetectedHardware,
            verified);
    }

    private BenchmarkResult BenchmarkStatsVizKde()
    {
        int numSamples = 20_000;
        int numGrid = 500;
        float[] samples = new float[numSamples];
        float[] grid = new float[numGrid];
        var rng = new Random(42);
        for (int i = 0; i < numSamples; i++) samples[i] = (float)rng.NextDouble() * 10.0f;
        for (int i = 0; i < numGrid; i++) grid[i] = i * 0.02f;

        float[] cpuDensity = new float[numGrid];
        float[] gpuDensity = new float[numGrid];

        // Warmup
        Glacier.StatsViz.Compute.GpuStatsAccelerator.EvaluateKde(samples, grid, 0.5f, cpuDensity, Glacier.StatsViz.Core.GpuTarget.Cpu);
        Glacier.StatsViz.Compute.GpuStatsAccelerator.EvaluateKde(samples, grid, 0.5f, gpuDensity, Glacier.StatsViz.Core.GpuTarget.Auto);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            Glacier.StatsViz.Compute.GpuStatsAccelerator.EvaluateKde(samples, grid, 0.5f, cpuDensity, Glacier.StatsViz.Core.GpuTarget.Cpu);
        sw.Stop();
        double cpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        sw.Restart();
        for (int i = 0; i < 5; i++)
            Glacier.StatsViz.Compute.GpuStatsAccelerator.EvaluateKde(samples, grid, 0.5f, gpuDensity, Glacier.StatsViz.Core.GpuTarget.Auto);
        sw.Stop();
        double gpuMs = sw.Elapsed.TotalMilliseconds / 5.0;

        bool verified = Math.Abs(cpuDensity[0] - gpuDensity[0]) < 1e-3f;
        double evaluationsPerSec = ((double)numSamples * numGrid) / (gpuMs * 1000.0);

        return new BenchmarkResult(
            "Glacier.StatsViz",
            "Gaussian Kernel Density Estimation",
            "20,000 samples x 500 grid points (10M evals)",
            cpuMs,
            gpuMs,
            cpuMs / Math.Max(gpuMs, 0.001),
            "Eval Rate (M evals/s)",
            evaluationsPerSec,
            DetectedHardware,
            verified);
    }
}
