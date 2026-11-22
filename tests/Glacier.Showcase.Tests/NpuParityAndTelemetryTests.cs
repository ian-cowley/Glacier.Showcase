using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Inference.Config;
using Glacier.Inference.Hardware;
using Glacier.Serve.Serialization;
using Glacier.Showcase.Components.Pages;
using Glacier.Showcase.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glacier.Showcase.Tests;

public sealed class NpuParityAndTelemetryTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public ConcurrentQueue<(HttpMethod Method, string Url, string Body)> Requests { get; } = new();

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : "";
            Requests.Enqueue((request.Method, request.RequestUri?.ToString() ?? "", body));
            return await _handler(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static DeviceInfo CreateNpuDevice() => new()
    {
        Id = "amd-xdna2",
        Index = 2,
        Name = "AMD XDNA 2 NPU (Ryzen AI 50 TOPS)",
        Vendor = GpuVendor.Amd,
        DedicatedVramBytes = 0,
        SharedVramBytes = 16_642_998_272UL, // 15.5 GB
        IsDisplayDevice = false,
        IsNpu = true,
        SupportedEngines = [InferenceEngineType.NpuSidecar],
        RecommendedEngine = InferenceEngineType.NpuSidecar,
        SafetyNotes = "NPU sidecar"
    };

    [Fact]
    public void FormatDeviceMemory_FormatsCpuNpuIgpuAndDgpuCorrectly()
    {
        var npu = CreateNpuDevice();
        var cpu = new DeviceInfo
        {
            Id = "cpu",
            Index = 0,
            Name = "AMD Ryzen AI 9 HX 370",
            Vendor = GpuVendor.Cpu,
            DedicatedVramBytes = 0,
            SharedVramBytes = 32UL * 1024 * 1024 * 1024,
            IsDisplayDevice = false,
            SupportedEngines = [InferenceEngineType.Cpu],
            RecommendedEngine = InferenceEngineType.Cpu,
            SafetyNotes = "CPU"
        };
        var igpu = new DeviceInfo
        {
            Id = "amd-radeon-890m",
            Index = 1,
            Name = "AMD Radeon 890M",
            Vendor = GpuVendor.Amd,
            DedicatedVramBytes = 512UL * 1024 * 1024,
            SharedVramBytes = 16UL * 1024 * 1024 * 1024,
            IsDisplayDevice = false,
            SupportedEngines = [InferenceEngineType.DirectML],
            RecommendedEngine = InferenceEngineType.DirectML,
            SafetyNotes = "iGPU"
        };
        var dgpu = new DeviceInfo
        {
            Id = "nvidia-rtx-4060",
            Index = 0,
            Name = "NVIDIA GeForce RTX 4060",
            Vendor = GpuVendor.Nvidia,
            DedicatedVramBytes = 8UL * 1024 * 1024 * 1024,
            SharedVramBytes = 16UL * 1024 * 1024 * 1024,
            IsDisplayDevice = true,
            SupportedEngines = [InferenceEngineType.BareMetal],
            RecommendedEngine = InferenceEngineType.BareMetal,
            SafetyNotes = "dGPU"
        };

        Assert.Equal("15.5 GB NPU Cap", MissionControlService.FormatDeviceMemory(npu));
        Assert.Equal("32.0 GB RAM", MissionControlService.FormatDeviceMemory(cpu));
        Assert.Equal("16.0 GB Unified", MissionControlService.FormatDeviceMemory(igpu));
        Assert.Equal("8.0 GB VRAM", MissionControlService.FormatDeviceMemory(dgpu));
    }

    [Fact]
    public void MissionControl_RendersNpuCapOptionAndTelemetryStrip()
    {
        NpuSidecarDiscovery.InvalidateCache();
        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"embeddinggemma"}]}"""));
            }
            if (url.EndsWith("/health", StringComparison.Ordinal))
            {
                var health = new HealthCheckResponse
                {
                    Status = "ok",
                    Devices =
                    [
                        new AcceleratorDeviceStatus { Id = "amd-xdna2", Name = "AMD XDNA 2 NPU", IsNpu = true, SharedVramGb = 15.5 }
                    ],
                    NpuDevices =
                    [
                        new AcceleratorDeviceStatus { Id = "amd-xdna2", Name = "AMD XDNA 2 NPU", IsNpu = true, SharedVramGb = 15.5 }
                    ],
                    NpuSidecar = new NpuSidecarStatus
                    {
                        Available = true,
                        BaseUrl = "http://127.0.0.1:52625",
                        Runtime = "FastFlowLM",
                        Models = ["embeddinggemma"],
                        LatencyMs = 4.2
                    }
                };
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(health, ServeJsonContext.Default.HealthCheckResponse)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var httpClient = new HttpClient(handler);
        using var svc = new MissionControlService(new MissionControlTelemetryOptions
        {
            HttpClient = httpClient,
            ServeHealthUrl = "http://127.0.0.1:11435/health",
            NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]
        });

        svc.SetDevicesForTesting(
        [
            new DeviceInfo
            {
                Id = "nvidia-rtx-4060",
                Index = 0,
                Name = "NVIDIA RTX 4060",
                Vendor = GpuVendor.Nvidia,
                DedicatedVramBytes = 8UL * 1024 * 1024 * 1024,
                SharedVramBytes = 16UL * 1024 * 1024 * 1024,
                IsDisplayDevice = true,
                SupportedEngines = [InferenceEngineType.BareMetal],
                RecommendedEngine = InferenceEngineType.BareMetal,
                SafetyNotes = "dGPU"
            },
            CreateNpuDevice()
        ]);

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(svc);

        var cut = ctx.RenderComponent<MissionControl>();
        cut.WaitForAssertion(() =>
        {
            string markup = cut.Markup;
            Assert.Contains("15.5 GB NPU Cap", markup);
            Assert.DoesNotContain("AMD XDNA 2 NPU (Ryzen AI 50 TOPS) (0.0 GB VRAM)", markup);
            Assert.Contains("FastFlowLM", markup);
            Assert.Contains("embeddinggemma", markup);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MissionControlService_RefreshAcceleratorTelemetryAsync_PopulatesOnline_AndDegradesOffline()
    {
        NpuSidecarDiscovery.InvalidateCache();
        using var onlineHandler = new StubHttpMessageHandler((req, _) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url == "http://127.0.0.1:52625/v1/models")
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"embeddinggemma"}]}"""));
            }
            if (url == "http://127.0.0.1:11435/health")
            {
                var health = new HealthCheckResponse
                {
                    Status = "ok",
                    NpuDevices = [new AcceleratorDeviceStatus { Id = "amd-xdna2", IsNpu = true, SharedVramGb = 15.5 }],
                    NpuSidecar = new NpuSidecarStatus { Available = true, Runtime = "FastFlowLM", Models = ["embeddinggemma"] }
                };
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(health, ServeJsonContext.Default.HealthCheckResponse)));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var onlineClient = new HttpClient(onlineHandler);
        using var onlineSvc = new MissionControlService(new MissionControlTelemetryOptions
        {
            HttpClient = onlineClient,
            ServeHealthUrl = "http://127.0.0.1:11435/health",
            NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]
        });

        await onlineSvc.RefreshAcceleratorTelemetryAsync();
        Assert.NotNull(onlineSvc.NpuSidecar);
        Assert.Equal("embeddinggemma", onlineSvc.NpuSidecar!.PrimaryModel);
        Assert.NotNull(onlineSvc.ServeHealth);
        Assert.Equal("ok", onlineSvc.ServeHealth!.Status);

        NpuSidecarDiscovery.InvalidateCache();
        using var offlineHandler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.AbsolutePath == "/health")
            {
                return Task.FromResult(JsonResponse("{ invalid json", HttpStatusCode.OK));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var offlineClient = new HttpClient(offlineHandler);
        using var offlineSvc = new MissionControlService(new MissionControlTelemetryOptions
        {
            HttpClient = offlineClient,
            ServeHealthUrl = "http://127.0.0.1:11435/health",
            NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]
        });

        await offlineSvc.RefreshAcceleratorTelemetryAsync();
        Assert.Null(offlineSvc.NpuSidecar);
        Assert.Null(offlineSvc.ServeHealth);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_WithoutApiKey_FallsBackToNpuSidecar_AndThrowsHelpfulErrorWhenOffline()
    {
        NpuSidecarDiscovery.InvalidateCache();
        using var npuHandler = new StubHttpMessageHandler((req, _) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse("""{"data":[{"id":"qwen3-4b"},{"id":"embeddinggemma"}]}"""));
            }
            if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                float[] vec = new float[768];
                vec[0] = 3.0f;
                vec[1] = 4.0f;
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } })));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var npuHttp = new HttpClient(npuHandler);
        using var npuClient = new GeminiEmbeddingClient(
            npuHttp,
            apiKey: "",
            npuEndpoint: null,
            npuEmbedModel: null,
            npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);

        float[] embedding = await npuClient.GetEmbeddingAsync("cozy flat near Hyde Park");
        Assert.Equal(768, embedding.Length);
        Assert.Equal("NpuSidecar", npuClient.ActiveBackend);
        Assert.Equal(768, npuClient.LastEmbeddingDimensions);
        // Normalized 3,4,0... -> 0.6, 0.8
        Assert.InRange(embedding[0], 0.59f, 0.61f);
        Assert.InRange(embedding[1], 0.79f, 0.81f);

        // Offline NPU + missing API key -> clear InvalidOperationException
        NpuSidecarDiscovery.InvalidateCache();
        using var offlineHandler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("Refused"));
        using var offlineHttp = new HttpClient(offlineHandler);
        using var offlineClient = new GeminiEmbeddingClient(
            offlineHttp,
            apiKey: "",
            npuEndpoint: null,
            npuEmbedModel: null,
            npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => offlineClient.GetEmbeddingAsync("test"));
        Assert.Contains("GEMINI_API_KEY", ex.Message);
        Assert.Contains("NPU", ex.Message);

        // With API key -> routes to Gemini embedContent
        using var geminiHandler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri?.Host == "generativelanguage.googleapis.com")
            {
                return Task.FromResult(JsonResponse("""{"embedding":{"values":[0.1,0.2,0.3,0.4]}}"""));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var geminiHttp = new HttpClient(geminiHandler);
        using var geminiClient = new GeminiEmbeddingClient(geminiHttp, apiKey: "test-key-placeholder");

        float[] gVec = await geminiClient.GetEmbeddingAsync("hello");
        Assert.Equal(4, gVec.Length);
        Assert.Equal("Gemini", geminiClient.ActiveBackend);
    }

    [Theory]
    [InlineData(768)]
    [InlineData(64)]
    public async Task AirbnbDataService_SemanticSearchAsync_SizesVectorStorageDynamically(int dims)
    {
        NpuSidecarDiscovery.InvalidateCache();
        float[] MakeUnitVector(int bucket)
        {
            var v = new float[dims];
            v[bucket % dims] = 1.0f;
            return v;
        }

        using var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":[{"id":"embeddinggemma"}]}""");
            }
            if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                string body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "";
                int bucket = body.Contains("quiet garden", StringComparison.OrdinalIgnoreCase) ||
                             body.Contains("serene courtyard", StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : body.Contains("nightlife", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                return JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = MakeUnitVector(bucket) } } }));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiEmbeddingClient(
            http,
            apiKey: "",
            npuEndpoint: "http://127.0.0.1:52625",
            npuEmbedModel: "embeddinggemma");

        var dataService = new AirbnbDataService(client);
        var candidates = new List<PropertyMatch>
        {
            new() { Id = "1", Name = "Shoreditch Loft", Description = "Vibrant nightlife and clubbing hub", Neighbourhood = "Hackney" },
            new() { Id = "2", Name = "Kensington Mews", Description = "Serene courtyard retreat with flowers", Neighbourhood = "Kensington" },
            new() { Id = "3", Name = "City Studio", Description = "Modern glass tower near office blocks", Neighbourhood = "City" }
        };

        var matches = await dataService.SemanticSearchAsync(candidates, "quiet garden escape", topK: 2);
        Assert.Equal(2, matches.Count);
        Assert.Equal("2", matches[0].Id);
        Assert.True(matches[0].VibeMatchScore > 0.9f);

        // Empty candidate list returns cleanly
        var emptyMatches = await dataService.SemanticSearchAsync(new List<PropertyMatch>(), "quiet garden escape", topK: 5);
        Assert.Empty(emptyMatches);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_LiveModelDimensionSwap_768To128_RecoversOnFirstCall()
    {
        NpuSidecarDiscovery.InvalidateCache();
        int currentDims = 768;
        string currentModel = "embeddinggemma";

        using var handler = new StubHttpMessageHandler((req, _) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse($$"""{"data":[{"id":"qwen3.5-9b"},{"id":"{{currentModel}}"}]}"""));
            }
            if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                float[] vec = new float[currentDims];
                vec[0] = 1.0f;
                return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } })));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiEmbeddingClient(
            http,
            apiKey: "",
            npuEndpoint: null,
            npuEmbedModel: null,
            npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);

        // Initial 768d call caches _npuModel
        float[] first = await client.GetEmbeddingAsync("cozy flat");
        Assert.Equal(768, first.Length);
        Assert.Equal(768, client.LastEmbeddingDimensions);

        // Live swap on NPU sidecar to 128d model (chosen over qwen3.5-9b via bge- token)
        currentDims = 128;
        currentModel = "bge-m3-npu";

        // Very first call after swap must transparently re-probe & recover without throwing
        float[] swapped = await client.GetEmbeddingAsync("cozy flat after swap");
        Assert.Equal(128, swapped.Length);
        Assert.Equal(128, client.LastEmbeddingDimensions);
        Assert.Equal("NpuSidecar", client.ActiveBackend);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_ConcurrentLiveModelDimensionSwap_768To128_ZeroExceptionsAndCleanConvergence()
    {
        for (int round = 0; round < 5; round++)
        {
            NpuSidecarDiscovery.InvalidateCache();
            int embedRequestCount = 0;
            int currentDims = 768;
            string currentModel = "embeddinggemma";

            using var handler = new StubHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    int dimsSnapshot = Volatile.Read(ref currentDims);
                    string modelSnapshot = dimsSnapshot == 128 ? "bge-m3-npu" : currentModel;
                    return JsonResponse($$"""{"data":[{"id":"qwen3.5-9b"},{"id":"{{modelSnapshot}}"}]}""");
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    await Task.Delay(10, ct).ConfigureAwait(false);
                    int seq = Interlocked.Increment(ref embedRequestCount);
                    if (seq > 6)
                    {
                        Volatile.Write(ref currentDims, 128);
                    }

                    int dims = Volatile.Read(ref currentDims);
                    float[] vec = new float[dims];
                    vec[0] = 1.0f;
                    return JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } }));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var http = new HttpClient(handler);
            using var client = new GeminiEmbeddingClient(
                http,
                apiKey: "",
                npuEndpoint: null,
                npuEmbedModel: null,
                npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);

            // Warm initial 768d cache (round 0..2), and test cold-start concurrency on rounds 3..4
            if (round < 3)
            {
                float[] initial = await client.GetEmbeddingAsync("warmup");
                Assert.Equal(768, initial.Length);
            }

            const int concurrentCallers = 48;
            var tasks = new Task<float[]>[concurrentCallers];
            for (int i = 0; i < concurrentCallers; i++)
            {
                string prompt = $"concurrent-query-{round}-{i}";
                tasks[i] = Task.Run(() => client.GetEmbeddingAsync(prompt));
            }

            float[][] results = await Task.WhenAll(tasks);
            Assert.Equal(concurrentCallers, results.Length);
            foreach (float[] vec in results)
            {
                Assert.True(vec.Length == 768 || vec.Length == 128, $"Unexpected embedding length {vec.Length}");
            }

            float[] converged = await client.GetEmbeddingAsync("post-swap-check");
            Assert.Equal(128, converged.Length);
            Assert.Equal(128, client.LastEmbeddingDimensions);
            Assert.Equal("NpuSidecar", client.ActiveBackend);

            client.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetEmbeddingAsync("after-dispose"));
            using HttpResponseMessage callerResp = await http.GetAsync("http://127.0.0.1:52625/v1/models");
            Assert.Equal(HttpStatusCode.OK, callerResp.StatusCode);
        }
    }

    private static async Task<(float[]? Vector, Exception? Exception)> CaptureEmbeddingOutcomeAsync(Task<float[]> task)
    {
        try
        {
            return (await task.ConfigureAwait(false), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private static void AssertValidFiniteUnitVector(float[] vec, params int[] allowedLengths)
    {
        Assert.Contains(vec.Length, allowedLengths);
        double sumSq = 0.0;
        foreach (float f in vec)
        {
            Assert.True(float.IsFinite(f), "Embedding vector contains non-finite component.");
            sumSq += (double)f * f;
        }
        Assert.InRange(Math.Sqrt(sumSq), 0.99, 1.01);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_ColdStartConcurrentDispose_NoDeadlockAndOnlyVectorOrObjectDisposed()
    {
        for (int round = 0; round < 10; round++)
        {
            NpuSidecarDiscovery.InvalidateCache();
            bool useExplicitEndpoint = (round & 1) == 1;

            using var handler = new StubHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    await Task.Delay(25, ct).ConfigureAwait(false);
                    return JsonResponse("""{"data":[{"id":"embeddinggemma"}]}""");
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    float[] vec = new float[768];
                    vec[0] = 1.0f;
                    return JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } }));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var http = new HttpClient(handler);
            var client = useExplicitEndpoint
                ? new GeminiEmbeddingClient(
                    http,
                    apiKey: "",
                    npuEndpoint: "http://127.0.0.1:52625",
                    npuEmbedModel: "embeddinggemma")
                : new GeminiEmbeddingClient(
                    http,
                    apiKey: "",
                    npuEndpoint: null,
                    npuEmbedModel: null,
                    npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);

            const int concurrentCallers = 20;
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerTasks = new Task<(float[]? Vector, Exception? Exception)>[concurrentCallers];
            for (int i = 0; i < concurrentCallers; i++)
            {
                string prompt = $"cold-dispose-{round}-{i}";
                callerTasks[i] = Task.Run(async () =>
                {
                    await startGate.Task.ConfigureAwait(false);
                    return await CaptureEmbeddingOutcomeAsync(client.GetEmbeddingAsync(prompt)).ConfigureAwait(false);
                });
            }

            Task disposeTask = Task.Run(async () =>
            {
                await startGate.Task.ConfigureAwait(false);
                int delayMs = (round % 3) * 4;
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
                client.Dispose();
            });

            startGate.SetResult();

            var outcomes = await Task.WhenAll(callerTasks).WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            foreach (var (vec, ex) in outcomes)
            {
                if (ex != null)
                {
                    Assert.IsType<ObjectDisposedException>(ex);
                }
                else
                {
                    Assert.NotNull(vec);
                    AssertValidFiniteUnitVector(vec!, 768);
                }
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetEmbeddingAsync("post-dispose"));
            using HttpResponseMessage callerResp = await http.GetAsync("http://127.0.0.1:52625/v1/models");
            Assert.Equal(HttpStatusCode.OK, callerResp.StatusCode);
        }
    }

    [Fact]
    public async Task GeminiEmbeddingClient_WarmStartMidSwapConcurrentDispose_NoStaleInvalidOperationOrHang()
    {
        for (int round = 0; round < 10; round++)
        {
            NpuSidecarDiscovery.InvalidateCache();
            bool useExplicitEndpoint = (round & 1) == 1;
            int embedRequestCount = 0;
            int currentDims = 768;
            GeminiEmbeddingClient? clientRef = null;

            using var handler = new StubHttpMessageHandler(async (req, ct) =>
            {
                string url = req.RequestUri?.ToString() ?? "";
                if (url.EndsWith("/v1/models", StringComparison.Ordinal))
                {
                    int dimsSnapshot = Volatile.Read(ref currentDims);
                    string modelSnapshot = dimsSnapshot == 128 ? "bge-m3-npu" : "embeddinggemma";
                    return JsonResponse($$"""{"data":[{"id":"qwen3.5-9b"},{"id":"{{modelSnapshot}}"}]}""");
                }
                if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
                {
                    await Task.Delay(15, ct).ConfigureAwait(false);
                    int seq = Interlocked.Increment(ref embedRequestCount);
                    if (seq > 6)
                    {
                        Volatile.Write(ref currentDims, 128);
                    }

                    int dims = Volatile.Read(ref currentDims);
                    float[] vec = new float[dims];
                    vec[0] = 1.0f;
                    var response = JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } }));
                    if (round >= 8 && seq == 8)
                    {
                        _ = Task.Run(() => Volatile.Read(ref clientRef)?.Dispose());
                    }
                    return response;
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            using var http = new HttpClient(handler);
            var client = useExplicitEndpoint
                ? new GeminiEmbeddingClient(
                    http,
                    apiKey: "",
                    npuEndpoint: "http://127.0.0.1:52625",
                    npuEmbedModel: null)
                : new GeminiEmbeddingClient(
                    http,
                    apiKey: "",
                    npuEndpoint: null,
                    npuEmbedModel: null,
                    npuCandidates: [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]);
            Volatile.Write(ref clientRef, client);

            float[] warmup = await client.GetEmbeddingAsync("warmup");
            AssertValidFiniteUnitVector(warmup, 768);

            const int concurrentCallers = 24;
            var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var callerTasks = new Task<(float[]? Vector, Exception? Exception)>[concurrentCallers];
            for (int i = 0; i < concurrentCallers; i++)
            {
                string prompt = $"warm-swap-dispose-{round}-{i}";
                callerTasks[i] = Task.Run(async () =>
                {
                    await startGate.Task.ConfigureAwait(false);
                    return await CaptureEmbeddingOutcomeAsync(client.GetEmbeddingAsync(prompt)).ConfigureAwait(false);
                });
            }

            Task disposeTask = Task.Run(async () =>
            {
                await startGate.Task.ConfigureAwait(false);
                int disposeDelayMs = 10 + (round % 4) * 6;
                await Task.Delay(disposeDelayMs).ConfigureAwait(false);
                client.Dispose();
            });

            startGate.SetResult();

            var outcomes = await Task.WhenAll(callerTasks).WaitAsync(TimeSpan.FromSeconds(5));
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));

            foreach (var (vec, ex) in outcomes)
            {
                if (ex != null)
                {
                    Assert.IsType<ObjectDisposedException>(ex);
                }
                else
                {
                    Assert.NotNull(vec);
                    AssertValidFiniteUnitVector(vec!, 768, 128);
                }
            }

            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetEmbeddingAsync("post-dispose"));
            using HttpResponseMessage callerResp = await http.GetAsync("http://127.0.0.1:52625/v1/models");
            Assert.Equal(HttpStatusCode.OK, callerResp.StatusCode);
        }
    }

    [Fact]
    public void GeminiEmbeddingClient_SelectEmbeddingModelId_PrefersBgeM3OverChatModel()
    {
        string selected = GeminiEmbeddingClient.SelectEmbeddingModelId(null, ["qwen3.5-9b", "bge-m3-npu"]);
        Assert.Equal("bge-m3-npu", selected);
    }

    [Fact]
    public async Task MissionControlService_RunLiveInferenceAsync_NpuDevice_ThrowsNotSupportedException()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        using var svc = new MissionControlService(new MissionControlTelemetryOptions
        {
            HttpClient = http
        });
        svc.SetDevicesForTesting([CreateNpuDevice()]);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
            svc.RunLiveInferenceAsync("any-model", "hello", "amd-xdna2", (_, _) => { }));
        Assert.Contains("NPU", ex.Message);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_SoloTarpitTimeout_WrapsInInvalidOperationException_WhileCallerCancelPropagates()
    {
        NpuSidecarDiscovery.InvalidateCache();
        bool tarpitEmbeddings = false;
        using var handler = new StubHttpMessageHandler(async (req, ct) =>
        {
            string url = req.RequestUri?.ToString() ?? "";
            if (url.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                return JsonResponse("""{"data":[{"id":"embeddinggemma"}]}""");
            }
            if (url.EndsWith("/v1/embeddings", StringComparison.Ordinal))
            {
                if (Volatile.Read(ref tarpitEmbeddings))
                {
                    await Task.Delay(1500, ct).ConfigureAwait(false);
                }
                float[] vec = new float[8];
                vec[0] = 1.0f;
                return JsonResponse(JsonSerializer.Serialize(new { data = new[] { new { embedding = vec } } }));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) };
        using var client = new GeminiEmbeddingClient(
            http,
            apiKey: "",
            npuEndpoint: "http://127.0.0.1:52625",
            npuEmbedModel: "embeddinggemma");

        // Warm cache first, then enable tarpit on /v1/embeddings only
        float[] initial = await client.GetEmbeddingAsync("warmup");
        Assert.Equal(8, initial.Length);

        Volatile.Write(ref tarpitEmbeddings, true);
        var warmEx = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetEmbeddingAsync("solo tarpit warm"));
        Assert.Contains("GEMINI_API_KEY", warmEx.Message);
        Assert.Contains("NPU", warmEx.Message);

        // Cold-start solo tarpit also wraps in InvalidOperationException
        var coldEx = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetEmbeddingAsync("solo tarpit cold"));
        Assert.Contains("GEMINI_API_KEY", coldEx.Message);

        // Caller-initiated cancellation still propagates as OperationCanceledException
        using var callerCts = new CancellationTokenSource(25);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetEmbeddingAsync("caller cancel", callerCts.Token));
    }
}
