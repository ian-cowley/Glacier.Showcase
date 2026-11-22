namespace Glacier.Showcase.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Glacier.Inference.Hardware;
using Glacier.Showcase.Components.Pages;
using Glacier.Showcase.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class Tier5M7Phase2HardeningTests : IDisposable
{
    private sealed class StubShowcaseHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubShowcaseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(request, cancellationToken);
    }

    public Tier5M7Phase2HardeningTests() => NpuSidecarDiscovery.InvalidateCache();
    public void Dispose() => NpuSidecarDiscovery.InvalidateCache();

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GeminiEmbeddingClient_CachedModelStall_FailsWithinSingleTimeoutWithoutSoloTarpit()
    {
        int embedCalls = 0;
        int stall = 0;
        using var handler = new StubShowcaseHandler(async (req, ct) =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, """{"data":[{"id":"embeddinggemma"}]}""");
            if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref embedCalls);
                if (Volatile.Read(ref stall) == 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
                return Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0,0.0,0.0,0.0]}]}""");
            }
            return Json(HttpStatusCode.NotFound, "{}") ;
        });

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(250) };
        using var client = new GeminiEmbeddingClient(http, apiKey: "", npuEndpoint: "http://127.0.0.1:52625", npuEmbedModel: "embeddinggemma");

        // Warm cache
        float[] ok = await client.GetEmbeddingAsync("warm");
        Assert.Equal(4, ok.Length);

        // Stall cached model -> must fail within a single timeout without double-delaying via CreateAutoAsync(5s)
        Volatile.Write(ref stall, 1);
        int callsBefore = Volatile.Read(ref embedCalls);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.GetEmbeddingAsync("stalled"));
        sw.Stop();

        Assert.Equal(1, Volatile.Read(ref embedCalls) - callsBefore);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(1500), $"Expected single timeout failure (<1500ms), actual: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task GeminiEmbeddingClient_ConcurrentDimSwap768To128_NeverThrowsOrCorruptsState()
    {
        int currentDim = 768;
        using var handler = new StubShowcaseHandler((req, _) =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embeddinggemma"}]}"""));
            if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                int d = Volatile.Read(ref currentDim);
                var vec = new float[d];
                vec[0] = 1.0f;
                return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"data\":[{{\"index\":0,\"embedding\":{JsonSerializer.Serialize(vec)}}}]}}"));
            }
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiEmbeddingClient(http, apiKey: "", npuEndpoint: "http://127.0.0.1:52625", npuEmbedModel: "embeddinggemma");

        var initial = await client.GetEmbeddingAsync("init-768");
        Assert.Equal(768, initial.Length);
        Assert.Equal(768, client.LastEmbeddingDimensions);

        Volatile.Write(ref currentDim, 128);
        var tasks = Enumerable.Range(0, 8).Select(i => client.GetEmbeddingAsync($"q-{i}")).ToArray();
        float[][] results = await Task.WhenAll(tasks);

        Assert.All(results, v => Assert.Equal(128, v.Length));
        Assert.Equal(128, client.LastEmbeddingDimensions);
        Assert.Equal("NpuSidecar", client.ActiveBackend);
    }

    [Fact]
    public void MissionControl_DeterministicMockedProbe_EliminatesColdProbeRace()
    {
        using var handler = new StubShowcaseHandler((req, _) =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embeddinggemma"}]}"""));
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        });
        using var http = new HttpClient(handler);
        using var mc = new MissionControlService(new MissionControlTelemetryOptions
        {
            HttpClient = http,
            ServeHealthUrl = "http://127.0.0.1:11435/health",
            NpuCandidates = [("http://127.0.0.1:52625", NpuSidecarRuntime.FastFlowLM)]
        });

        using var ctx = new TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(mc);

        var cut = ctx.RenderComponent<MissionControl>();
        cut.WaitForAssertion(() => Assert.Equal(6, cut.FindAll("button.mc-tab").Count), TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(384)]
    [InlineData(768)]
    [InlineData(3072)]
    public async Task AirbnbDataService_DynamicDimsAndEmptyCandidates_ZeroCrashAndNullSafe(int dims)
    {
        using var handler = new StubShowcaseHandler((req, _) =>
        {
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(HttpStatusCode.OK, """{"data":[{"id":"embeddinggemma"}]}"""));
            if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                var vec = new float[dims];
                vec[0] = 1.0f;
                return Task.FromResult(Json(HttpStatusCode.OK, $"{{\"data\":[{{\"index\":0,\"embedding\":{JsonSerializer.Serialize(vec)}}}]}}"));
            }
            return Task.FromResult(Json(HttpStatusCode.NotFound, "{}"));
        });
        using var http = new HttpClient(handler);
        using var embedClient = new GeminiEmbeddingClient(http, apiKey: "", npuEndpoint: "http://127.0.0.1:52625", npuEmbedModel: "embeddinggemma");
        var airbnb = new AirbnbDataService(embedClient);

        // Empty candidates -> safe empty list
        var emptyRes = await airbnb.SemanticSearchAsync(new List<PropertyMatch>(), "cozy", topK: 3);
        Assert.Empty(emptyRes);

        var candidates = new List<PropertyMatch>
        {
            new() { Id = "1", Name = "Sunny Flat", Description = "Near park", Neighbourhood = "Camden", Price = 120 },
            new() { Id = "2", Name = "Quiet Studio", Description = "Great transit", Neighbourhood = "Soho", Price = 140 }
        };
        var ranked = await airbnb.SemanticSearchAsync(candidates, "sunny park flat", topK: 2);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public async Task GeminiEmbeddingClient_QueryStringEndpointWithoutExplicitModel_DiscoversServedModel()
    {
        var capturedUris = new List<string>();
        string? capturedModel = null;
        using var handler = new StubShowcaseHandler(async (req, ct) =>
        {
            capturedUris.Add(req.RequestUri?.ToString() ?? "");
            string path = req.RequestUri?.AbsolutePath ?? "";
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
                return Json(HttpStatusCode.OK, """{"data":[{"id":"embed-q"}]}""");
            if (path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase))
            {
                string body = req.Content != null ? await req.Content.ReadAsStringAsync(ct) : "{}";
                using var doc = JsonDocument.Parse(body);
                capturedModel = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : null;
                return Json(HttpStatusCode.OK, """{"data":[{"index":0,"embedding":[1.0,0.0,0.0,0.0]}]}""");
            }
            return Json(HttpStatusCode.NotFound, "{}");
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiEmbeddingClient(http, apiKey: "", npuEndpoint: "http://127.0.0.1:52625/v1?api-version=1", npuEmbedModel: null);

        float[] vec = await client.GetEmbeddingAsync("hello");
        Assert.Equal(4, vec.Length);
        Assert.Equal("embed-q", capturedModel);
        Assert.Contains("http://127.0.0.1:52625/v1/models?api-version=1", capturedUris);
        Assert.Contains("http://127.0.0.1:52625/v1/embeddings?api-version=1", capturedUris);
    }
}
