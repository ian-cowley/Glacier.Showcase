using System.Net.Http.Json;
using System.Text.Json;
using Glacier.Inference.Hardware;
using Glacier.Rag.Embeddings;

namespace Glacier.Showcase.Services;

public class GeminiEmbeddingClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string? _npuEndpoint;
    private readonly string? _npuEmbedModel;
    private readonly IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? _npuCandidates;
    private readonly SemaphoreSlim _npuInitLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _stateLock = new();
    private OpenAiEmbeddingModel? _npuModel;
    private string _activeBackend = "";
    private int? _lastEmbeddingDimensions;
    private int _disposed;
    private int _activeOps = 1; // initial ref owned by instance until Dispose()

    public string ActiveBackend
    {
        get { lock (_stateLock) return _activeBackend; }
        private set { lock (_stateLock) _activeBackend = value; }
    }
    public int? LastEmbeddingDimensions
    {
        get { lock (_stateLock) return _lastEmbeddingDimensions; }
        private set { lock (_stateLock) _lastEmbeddingDimensions = value; }
    }

    public GeminiEmbeddingClient(HttpClient httpClient, string apiKey)
        : this(httpClient, apiKey, npuEndpoint: null, npuEmbedModel: null, npuCandidates: null)
    {
    }

    public GeminiEmbeddingClient(
        HttpClient httpClient,
        string apiKey,
        string? npuEndpoint = null,
        string? npuEmbedModel = null,
        IReadOnlyList<(string BaseUrl, NpuSidecarRuntime Runtime)>? npuCandidates = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? "";
        _npuEndpoint = string.IsNullOrWhiteSpace(npuEndpoint) ? null : npuEndpoint.Trim();
        _npuEmbedModel = string.IsNullOrWhiteSpace(npuEmbedModel) ? null : npuEmbedModel.Trim();
        _npuCandidates = npuCandidates;
    }

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return await GetNpuEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        }

        var request = new
        {
            model = "models/gemini-embedding-001",
            content = new
            {
                parts = new[] { new { text = text } }
            }
        };

        var response = await _httpClient.PostAsJsonAsync(
            $"https://generativelanguage.googleapis.com/v1/models/gemini-embedding-001:embedContent?key={_apiKey}",
            request,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Gemini API Error (EmbedContent): {response.StatusCode}\nDetails: {errorBody}");
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
        var values = json.GetProperty("embedding").GetProperty("values").EnumerateArray();

        var result = values.Select(v => v.GetSingle()).ToArray();
        lock (_stateLock)
        {
            _activeBackend = "Gemini";
            _lastEmbeddingDimensions = result.Length;
        }
        return result;
    }

    private static readonly string[] s_embeddingModelTokens =
    [
        "embed", "bge-", "e5-", "gte-", "minilm", "snowflake"
    ];

    private async Task<float[]> GetNpuEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        EnterActiveOp();
        try
        {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            (OpenAiEmbeddingModel model, bool hadCachedModel) = await EnsureNpuModelAsync(cancellationToken).ConfigureAwait(false);
            using var linkedCts = CreateLinkedDisposeCts(cancellationToken);
            try
            {
                float[] vector = await model.GenerateEmbeddingAsync(text, linkedCts.Token).ConfigureAwait(false);
                if (Volatile.Read(ref _disposed) == 0)
                {
                    lock (_stateLock)
                    {
                        _activeBackend = "NpuSidecar";
                        _lastEmbeddingDimensions = vector.Length;
                    }
                }
                return vector;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
                throw;
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
                throw;
            }
            catch (Exception ex) when (
                ex is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException ||
                (ex is ObjectDisposedException && Volatile.Read(ref _disposed) == 0))
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                await ResetNpuModelAsync(model).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (attempt == 0 && Volatile.Read(ref _disposed) == 0 &&
                    !cancellationToken.IsCancellationRequested &&
                    ((hadCachedModel && ex is HttpRequestException) ||
                     ex is InvalidOperationException or ObjectDisposedException))
                {
                    continue;
                }
                if (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                {
                    string targetDesc = !string.IsNullOrWhiteSpace(_npuEndpoint)
                        ? $"at '{_npuEndpoint}'"
                        : "endpoint";
                    throw new InvalidOperationException(
                        $"GEMINI_API_KEY is not configured and NPU embedding sidecar {targetDesc} timed out.",
                        ex);
                }
                throw;
            }
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        throw new InvalidOperationException("Failed to obtain embedding from NPU sidecar.");
        }
        finally
        {
            ExitActiveOp();
        }
    }

    private CancellationTokenSource CreateLinkedDisposeCts(CancellationToken cancellationToken)
    {
        try
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        }
        catch (ObjectDisposedException)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            throw;
        }
    }

    internal static string SelectEmbeddingModelId(string? explicitModelId, IReadOnlyList<string> discoveredModels)
    {
        if (!string.IsNullOrWhiteSpace(explicitModelId))
            return explicitModelId.Trim();

        foreach (var token in s_embeddingModelTokens)
        {
            foreach (var model in discoveredModels)
            {
                if (!string.IsNullOrWhiteSpace(model) && model.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return model.Trim();
            }
        }

        foreach (var model in discoveredModels)
        {
            if (!string.IsNullOrWhiteSpace(model))
                return model.Trim();
        }

        return "embeddinggemma";
    }

    private async Task<(OpenAiEmbeddingModel Model, bool WasCached)> EnsureNpuModelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var existing = Volatile.Read(ref _npuModel);
        if (existing != null)
            return (existing, true);

        using var linkedCts = CreateLinkedDisposeCts(cancellationToken);
        CancellationToken linkedToken = linkedCts.Token;

        try
        {
            await _npuInitLock.WaitAsync(linkedToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            ObjectDisposedException.ThrowIf(true, this);
            throw;
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0)
        {
            ObjectDisposedException.ThrowIf(true, this);
            throw;
        }

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var current = Volatile.Read(ref _npuModel);
            if (current != null)
                return (current, true);

            NpuSidecarEndpoint? endpoint = null;
            if (!string.IsNullOrWhiteSpace(_npuEndpoint))
            {
                endpoint = await NpuSidecarDiscovery.ProbeSingleAsync(
                    _httpClient,
                    _npuEndpoint,
                    NpuSidecarRuntime.CustomOpenAi,
                    TimeSpan.FromMilliseconds(500),
                    linkedToken).ConfigureAwait(false);

                if (endpoint == null)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    throw new InvalidOperationException(
                        $"GEMINI_API_KEY is not configured and NPU embedding sidecar at '{_npuEndpoint}' is not reachable.");
                }
            }
            else
            {
                endpoint = await NpuSidecarDiscovery.ProbeCachedAsync(
                    httpClient: _httpClient,
                    candidates: _npuCandidates,
                    perEndpointTimeout: TimeSpan.FromMilliseconds(500),
                    cancellationToken: linkedToken).ConfigureAwait(false);

                if (endpoint == null)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    throw new InvalidOperationException(
                        "GEMINI_API_KEY is not configured and no local NPU embedding sidecar is reachable.");
                }
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            string modelId = SelectEmbeddingModelId(
                _npuEmbedModel ?? Environment.GetEnvironmentVariable("GLACIER_NPU_EMBED_MODEL"),
                endpoint.Models);

            OpenAiEmbeddingModel created;
            try
            {
                created = await OpenAiEmbeddingModel.CreateAutoAsync(
                    endpoint.BaseUrl,
                    modelId,
                    httpClient: _httpClient,
                    timeout: TimeSpan.FromSeconds(5),
                    cancellationToken: linkedToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex) when (Volatile.Read(ref _disposed) == 0 && !cancellationToken.IsCancellationRequested)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                throw new InvalidOperationException(
                    $"GEMINI_API_KEY is not configured and NPU embedding sidecar at '{endpoint.BaseUrl}' timed out during model probe.",
                    ex);
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                created.Dispose();
                ObjectDisposedException.ThrowIf(true, this);
            }

            Interlocked.Exchange(ref _npuModel, created);
            if (Volatile.Read(ref _disposed) != 0)
            {
                Interlocked.CompareExchange(ref _npuModel, null, created);
                created.Dispose();
                ObjectDisposedException.ThrowIf(true, this);
            }

            return (created, false);
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) != 0 &&
            ex is OperationCanceledException or HttpRequestException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            ObjectDisposedException.ThrowIf(true, this);
            throw;
        }
        finally
        {
            try
            {
                _npuInitLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task ResetNpuModelAsync(OpenAiEmbeddingModel failedModel)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            await _npuInitLock.WaitAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        bool shouldDispose = false;
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (ReferenceEquals(_npuModel, failedModel))
            {
                NpuSidecarDiscovery.InvalidateCache();
                Volatile.Write(ref _npuModel, null);
                shouldDispose = true;
            }
        }
        finally
        {
            try
            {
                _npuInitLock.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (shouldDispose)
        {
            failedModel.Dispose();
        }
    }

    private void EnterActiveOp()
    {
        while (true)
        {
            int cur = Volatile.Read(ref _activeOps);
            if (cur <= 0 || Volatile.Read(ref _disposed) != 0)
            {
                ObjectDisposedException.ThrowIf(true, this);
            }
            if (Interlocked.CompareExchange(ref _activeOps, cur + 1, cur) == cur)
            {
                return;
            }
        }
    }

    private void ExitActiveOp()
    {
        if (Interlocked.Decrement(ref _activeOps) == 0)
        {
            _npuInitLock.Dispose();
            _disposeCts.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeCts.Cancel();
        Interlocked.Exchange(ref _npuModel, null)?.Dispose();
        ExitActiveOp();
    }
}
