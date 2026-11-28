using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics.Tensors;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;

namespace Glacier.Showcase.Services;

public class PropertyMatch
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Neighbourhood { get; set; } = "";
    public double Price { get; set; }
    public double ReviewScore { get; set; }
    public int ReviewsCount { get; set; }
    public float VibeMatchScore { get; set; }
}

public class AirbnbDataService : IDisposable
{
    internal const int MaxEmbeddingCacheEntries = 2048;

    private readonly GeminiEmbeddingClient _embeddingClient;
    private readonly string _londonCsvPath;

    // Cached HNSW Vector Search Index keyed by (Dimensions, CandidateFingerprint)
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private InMemoryVectorStorage? _cachedVectorStorage;
    private HnswVectorIndex? _cachedHnswIndex;
    private int _cachedIndexDims;
    private string? _cachedCandidateFingerprint;
    private int _disposed;

    // Bounded embedding cache keyed by (Dimensions, Text)
    private readonly object _cacheLock = new();
    private readonly Dictionary<(int Dims, string Text), float[]> _embeddingCache = new();
    private readonly Queue<(int Dims, string Text)> _embeddingCacheOrder = new();

    internal int CachedEmbeddingCount
    {
        get { lock (_cacheLock) return _embeddingCache.Count; }
    }

    internal int IndexRebuildCount { get; private set; }

    public AirbnbDataService(GeminiEmbeddingClient embeddingClient)
    {
        _embeddingClient = embeddingClient ?? throw new ArgumentNullException(nameof(embeddingClient));
        _londonCsvPath = ResolveListingsCsvPath();
    }

    private static string ResolveListingsCsvPath()
    {
        string? envPath = Environment.GetEnvironmentVariable("GLACIER_LISTINGS_CSV");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath.Trim());
        }

        string[] candidates =
        [
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "London_listings.csv"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "Data", "London_listings.csv"),
            Path.Combine(AppContext.BaseDirectory, "Data", "London_listings.csv"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data", "London_listings.csv")
        ];

        foreach (var candidate in candidates)
        {
            string fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return Path.GetFullPath(candidates[0]);
    }

    private void EnsureCsvExists()
    {
        if (!File.Exists(_londonCsvPath))
        {
            throw new FileNotFoundException(
                $"London listings dataset was not found at '{_londonCsvPath}'. Set GLACIER_LISTINGS_CSV or place London_listings.csv in ./Data/.",
                _londonCsvPath);
        }
    }

    /// <summary>
    /// Filters properties using Glacier.Polaris and returns basic details.
    /// </summary>
    public async Task<List<PropertyMatch>> FilterPropertiesAsync(
        double maxPrice,
        int minReviews,
        string? neighborhood = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCsvExists();

        // 1. Scan the CSV using Polaris LazyFrame
        var lf = DataFrame.ScanCsv(_londonCsvPath);

        // 2. Build the Expression pipeline
        var priceExpr = Expr.Col("price").Str().ReplaceAll("$", "").Str().ReplaceAll(",", "").Cast(typeof(double)).Alias("parsed_price");
        var reviewsExpr = Expr.Col("number_of_reviews").Cast(typeof(long)).Alias("parsed_reviews");
        var scoreExpr = Expr.Col("review_scores_rating").Cast(typeof(double)).Alias("parsed_score");

        LazyFrame filteredLf = lf.WithColumns(priceExpr, reviewsExpr, scoreExpr);

        var filterExpr = (Expr.Col("parsed_price") <= Expr.Lit(maxPrice)) &
                         (Expr.Col("parsed_reviews") >= Expr.Lit((long)minReviews));

        if (!string.IsNullOrEmpty(neighborhood))
        {
            filterExpr = filterExpr & Expr.Col("neighbourhood_cleansed").Str().Contains(neighborhood);
        }

        filteredLf = filteredLf
            .Filter(filterExpr)
            .Select(
                Expr.Col("id"),
                Expr.Col("name"),
                Expr.Col("description"),
                Expr.Col("neighbourhood_cleansed"),
                Expr.Col("parsed_price").Alias("price"),
                Expr.Col("parsed_score").Alias("review_scores_rating"),
                Expr.Col("parsed_reviews").Alias("number_of_reviews")
            )
            .Limit(limit);

        using var df = await filteredLf.Collect().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return MapRows(df);
    }

    /// <summary>
    /// Gets a set of candidate properties in a neighborhood for Vector Specialist semantic search.
    /// </summary>
    public async Task<List<PropertyMatch>> GetPropertiesInNeighborhoodAsync(
        string neighborhood,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCsvExists();

        var lf = DataFrame.ScanCsv(_londonCsvPath);

        var priceExpr = Expr.Col("price").Str().ReplaceAll("$", "").Str().ReplaceAll(",", "").Cast(typeof(double)).Alias("parsed_price");
        var reviewsExpr = Expr.Col("number_of_reviews").Cast(typeof(long)).Alias("parsed_reviews");
        var scoreExpr = Expr.Col("review_scores_rating").Cast(typeof(double)).Alias("parsed_score");

        var filteredLf = lf
            .WithColumns(priceExpr, reviewsExpr, scoreExpr)
            .Filter(Expr.Col("neighbourhood_cleansed").Str().Contains(neighborhood))
            .Select(
                Expr.Col("id"),
                Expr.Col("name"),
                Expr.Col("description"),
                Expr.Col("neighbourhood_cleansed"),
                Expr.Col("parsed_price").Alias("price"),
                Expr.Col("parsed_score").Alias("review_scores_rating"),
                Expr.Col("parsed_reviews").Alias("number_of_reviews")
            )
            .Limit(limit);

        using var df = await filteredLf.Collect().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return MapRows(df);
    }

    /// <summary>
    /// Embeds a subset of properties into a cached Glacier.Vector HnswVectorIndex and searches for a specific vibe.
    /// Supports dynamic embedding dimensionalities and candidate sets with automatic index replacement and disposal.
    /// </summary>
    public async Task<List<PropertyMatch>> SemanticSearchAsync(
        List<PropertyMatch> candidateProperties,
        string vibeDescription,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (candidateProperties == null || candidateProperties.Count == 0 || string.IsNullOrWhiteSpace(vibeDescription) || topK <= 0)
        {
            return [];
        }

        // 1. Generate embedding for the query "vibe" first to discover dimensionality dynamically
        // (supports 3072-dim Gemini, 768-dim NPU EmbeddingGemma, or any custom embedding model).
        var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(vibeDescription, cancellationToken).ConfigureAwait(false);
        if (queryEmbedding.Length == 0)
        {
            return [];
        }

        int dims = queryEmbedding.Length;
        string candidateFingerprint = ComputeCandidateFingerprint(candidateProperties);
        float[] normalizedQuery = NormalizeVectorCopy(queryEmbedding);

        // Fast path: if cached HNSW index already matches (dims, candidateFingerprint), search without re-embedding candidates.
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_cachedHnswIndex != null &&
                _cachedIndexDims == dims &&
                string.Equals(_cachedCandidateFingerprint, candidateFingerprint, StringComparison.Ordinal))
            {
                return MapSearchResults(_cachedHnswIndex.Search(normalizedQuery, topK));
            }
        }
        finally
        {
            _indexLock.Release();
        }

        // 2. Cache miss: generate candidate embeddings in parallel with bounded concurrency and bounded cache.
        var embeddedItems = new ConcurrentBag<(float[] Embedding, string Metadata)>();
        using var semaphore = new SemaphoreSlim(5);
        var tasks = candidateProperties.Select(async prop =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string textToEmbed = $"{prop.Name}. {prop.Description}";
                if (string.IsNullOrWhiteSpace(textToEmbed)) return;

                if (!TryGetCachedEmbedding(dims, textToEmbed, out var embedding))
                {
                    embedding = await _embeddingClient.GetEmbeddingAsync(textToEmbed, cancellationToken).ConfigureAwait(false);
                    if (embedding.Length > 0)
                    {
                        StoreCachedEmbedding(embedding.Length, textToEmbed, embedding);
                    }
                }

                if (embedding.Length == dims)
                {
                    var normalizedVec = NormalizeVectorCopy(embedding);
                    var metadata = JsonSerializer.Serialize(prop, ShowcaseJsonContext.Default.PropertyMatch);
                    embeddedItems.Add((normalizedVec, metadata));
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (embeddedItems.IsEmpty)
        {
            return [];
        }

        var itemsList = embeddedItems.ToList();

        // 3. Build or reuse HnswVectorIndex under lock, disposing prior storage+index on swap.
        await _indexLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_cachedHnswIndex == null ||
                _cachedIndexDims != dims ||
                !string.Equals(_cachedCandidateFingerprint, candidateFingerprint, StringComparison.Ordinal))
            {
                var newStorage = new InMemoryVectorStorage(dimensions: dims);
                var newIndex = new HnswVectorIndex(newStorage, initialCapacity: Math.Max(16, itemsList.Count));
                try
                {
                    foreach (var (embedding, metadata) in itemsList)
                    {
                        newIndex.Add(embedding, metadata);
                    }
                }
                catch
                {
                    newIndex.Dispose();
                    newStorage.Dispose();
                    throw;
                }

                var oldIndex = _cachedHnswIndex;
                var oldStorage = _cachedVectorStorage;
                _cachedHnswIndex = newIndex;
                _cachedVectorStorage = newStorage;
                _cachedIndexDims = dims;
                _cachedCandidateFingerprint = candidateFingerprint;
                IndexRebuildCount++;

                oldIndex?.Dispose();
                oldStorage?.Dispose();
            }

            // 4. Perform SIMD HNSW search on unit-normalized vectors (DotProduct == Cosine Similarity)
            var results = _cachedHnswIndex.Search(normalizedQuery, topK);
            return MapSearchResults(results);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private static List<PropertyMatch> MapSearchResults(SearchResult[] results)
    {
        var bestMatches = new List<PropertyMatch>(results.Length);
        foreach (var res in results)
        {
            if (string.IsNullOrEmpty(res.Metadata)) continue;
            var prop = JsonSerializer.Deserialize(res.Metadata, ShowcaseJsonContext.Default.PropertyMatch);
            if (prop != null)
            {
                prop.VibeMatchScore = res.Score;
                bestMatches.Add(prop);
            }
        }
        return bestMatches;
    }

    private bool TryGetCachedEmbedding(int dims, string text, out float[] embedding)
    {
        lock (_cacheLock)
        {
            if (_embeddingCache.TryGetValue((dims, text), out var found) && found.Length == dims)
            {
                embedding = found;
                return true;
            }
        }
        embedding = Array.Empty<float>();
        return false;
    }

    private void StoreCachedEmbedding(int dims, string text, float[] embedding)
    {
        if (dims <= 0 || embedding.Length != dims) return;

        var key = (dims, text);
        lock (_cacheLock)
        {
            if (_embeddingCache.ContainsKey(key))
            {
                _embeddingCache[key] = embedding;
                return;
            }

            while (_embeddingCache.Count >= MaxEmbeddingCacheEntries && _embeddingCacheOrder.TryDequeue(out var oldestKey))
            {
                _embeddingCache.Remove(oldestKey);
            }

            _embeddingCache[key] = embedding;
            _embeddingCacheOrder.Enqueue(key);
        }
    }

    private static float[] NormalizeVectorCopy(ReadOnlySpan<float> source)
    {
        var copy = new float[source.Length];
        source.CopyTo(copy);
        float normSq = TensorPrimitives.SumOfSquares(copy);
        if (normSq > 1e-12f && float.IsFinite(normSq))
        {
            float invNorm = 1.0f / MathF.Sqrt(normSq);
            TensorPrimitives.Multiply(copy, invNorm, copy);
        }
        return copy;
    }

    private static string ComputeCandidateFingerprint(List<PropertyMatch> candidates)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lenBuf = stackalloc byte[4];
        foreach (var p in candidates)
        {
            AppendStringHash(sha, p.Id, lenBuf);
            AppendStringHash(sha, p.Name, lenBuf);
            AppendStringHash(sha, p.Description, lenBuf);
            AppendStringHash(sha, p.Neighbourhood, lenBuf);
            AppendStringHash(sha, p.Price.ToString("R", CultureInfo.InvariantCulture), lenBuf);
            AppendStringHash(sha, p.ReviewScore.ToString("R", CultureInfo.InvariantCulture), lenBuf);
            AppendStringHash(sha, p.ReviewsCount.ToString(CultureInfo.InvariantCulture), lenBuf);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void AppendStringHash(IncrementalHash sha, string? value, Span<byte> lenBuf)
    {
        value ??= string.Empty;
        int maxBytes = Encoding.UTF8.GetByteCount(value);
        BitConverter.TryWriteBytes(lenBuf, maxBytes);
        sha.AppendData(lenBuf);
        if (maxBytes > 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            sha.AppendData(bytes);
        }
    }

    /// <summary>
    /// Maps a collected Polaris DataFrame to a list of PropertyMatch objects using typed Series access.
    /// </summary>
    private static List<PropertyMatch> MapRows(DataFrame df)
    {
        int rowCount = df.RowCount;
        var matches = new List<PropertyMatch>(rowCount);
        if (rowCount == 0) return matches;

        ISeries idCol = df.GetColumn("id");
        ISeries nameCol = df.GetColumn("name");
        ISeries descCol = df.GetColumn("description");
        ISeries neighCol = df.GetColumn("neighbourhood_cleansed");
        ISeries priceCol = df.GetColumn("price");
        ISeries revCol = df.GetColumn("number_of_reviews");
        ISeries scoreCol = df.GetColumn("review_scores_rating");

        var idStr = idCol as Utf8StringSeries;
        var idI64 = idCol as Int64Series;
        var idI32 = idCol as Int32Series;

        var nameStr = nameCol as Utf8StringSeries;
        var descStr = descCol as Utf8StringSeries;
        var neighStr = neighCol as Utf8StringSeries;

        var priceF64 = priceCol as Float64Series;
        var scoreF64 = scoreCol as Float64Series;
        var revI64 = revCol as Int64Series;
        var revI32 = revCol as Int32Series;

        ReadOnlySpan<double> priceSpan = priceF64 != null ? priceF64.Memory.Span : default;
        ReadOnlySpan<double> scoreSpan = scoreF64 != null ? scoreF64.Memory.Span : default;
        ReadOnlySpan<long> revI64Span = revI64 != null ? revI64.Memory.Span : default;
        ReadOnlySpan<int> revI32Span = revI32 != null ? revI32.Memory.Span : default;
        ReadOnlySpan<long> idI64Span = idI64 != null ? idI64.Memory.Span : default;
        ReadOnlySpan<int> idI32Span = idI32 != null ? idI32.Memory.Span : default;

        for (int i = 0; i < rowCount; i++)
        {
            string idValue;
            if (idStr != null)
            {
                idValue = idStr.ValidityMask.IsNull(i) ? "" : (idStr.GetString(i) ?? "");
            }
            else if (idI64 != null)
            {
                idValue = idI64.ValidityMask.IsNull(i) ? "" : idI64Span[i].ToString(CultureInfo.InvariantCulture);
            }
            else if (idI32 != null)
            {
                idValue = idI32.ValidityMask.IsNull(i) ? "" : idI32Span[i].ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                idValue = idCol.Get(i)?.ToString() ?? "";
            }

            string nameValue = nameStr != null
                ? (nameStr.ValidityMask.IsNull(i) ? "" : (nameStr.GetString(i) ?? ""))
                : (nameCol.Get(i)?.ToString() ?? "");

            string descValue = descStr != null
                ? (descStr.ValidityMask.IsNull(i) ? "" : (descStr.GetString(i) ?? ""))
                : (descCol.Get(i)?.ToString() ?? "");

            string neighValue = neighStr != null
                ? (neighStr.ValidityMask.IsNull(i) ? "" : (neighStr.GetString(i) ?? ""))
                : (neighCol.Get(i)?.ToString() ?? "");

            double priceValue = priceF64 != null
                ? (priceF64.ValidityMask.IsNull(i) ? 0.0 : priceSpan[i])
                : (priceCol.Get(i) is double p ? p : 0.0);

            double scoreValue = scoreF64 != null
                ? (scoreF64.ValidityMask.IsNull(i) ? 0.0 : scoreSpan[i])
                : (scoreCol.Get(i) is double s ? s : 0.0);

            int reviewsValue;
            if (revI64 != null)
            {
                reviewsValue = revI64.ValidityMask.IsNull(i) ? 0 : (int)revI64Span[i];
            }
            else if (revI32 != null)
            {
                reviewsValue = revI32.ValidityMask.IsNull(i) ? 0 : revI32Span[i];
            }
            else
            {
                reviewsValue = revCol.Get(i) is long r ? (int)r : (revCol.Get(i) is int r32 ? r32 : 0);
            }

            matches.Add(new PropertyMatch
            {
                Id = idValue,
                Name = nameValue,
                Description = descValue,
                Neighbourhood = neighValue,
                Price = priceValue,
                ReviewScore = scoreValue,
                ReviewsCount = reviewsValue
            });
        }

        return matches;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _indexLock.Wait();
        try
        {
            _cachedHnswIndex?.Dispose();
            _cachedHnswIndex = null;
            _cachedVectorStorage?.Dispose();
            _cachedVectorStorage = null;
            _cachedCandidateFingerprint = null;
            _cachedIndexDims = 0;
        }
        finally
        {
            _indexLock.Release();
            _indexLock.Dispose();
        }
    }
}
