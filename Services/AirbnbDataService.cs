using System.Text.Json;
using System.Collections.Concurrent;
using Glacier.Polaris;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;
using Glacier.Showcase.Services;

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

public class AirbnbDataService
{
    private readonly GeminiEmbeddingClient _embeddingClient;
    private readonly string _londonCsvPath;
    
    // Vector Search
    private VectorIndex? _vectorIndex;
    private readonly ConcurrentDictionary<string, float[]> _embeddingCache = new();
    
    public AirbnbDataService(GeminiEmbeddingClient embeddingClient)
    {
        _embeddingClient = embeddingClient;
        // The path to the CSV file is two levels up from the executing assembly, inside Data
        _londonCsvPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Data", "London_listings.csv");
    }

    /// <summary>
    /// Filters properties using Glacier.Polaris and returns basic details.
    /// </summary>
    public async Task<List<PropertyMatch>> FilterPropertiesAsync(double maxPrice, int minReviews, string? neighborhood = null, int limit = 100)
    {
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

        var df = await filteredLf.Collect();
        return MapRows(df);
    }

    /// <summary>
    /// Gets a set of candidate properties in a neighborhood for Vector Specialist semantic search.
    /// </summary>
    public async Task<List<PropertyMatch>> GetPropertiesInNeighborhoodAsync(string neighborhood, int limit = 100)
    {
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

        var df = await filteredLf.Collect();
        return MapRows(df);
    }

    /// <summary>
    /// Embeds a subset of properties into Glacier.Vector and searches for a specific vibe.
    /// This demonstrates dynamic semantic search over unstructured text.
    /// </summary>
    public async Task<List<PropertyMatch>> SemanticSearchAsync(List<PropertyMatch> candidateProperties, string vibeDescription, int topK = 5)
    {
        // 1. Initialize Vector Storage (3072 is Gemini embedding size)
        var storage = new InMemoryVectorStorage(dimensions: 3072);
        _vectorIndex = new VectorIndex(storage);

        // 2. Generate embeddings in parallel with rate limiting
        var semaphore = new SemaphoreSlim(5); // Process 5 at a time to be safe with rate limits
        var tasks = candidateProperties.Select(async prop =>
        {
            await semaphore.WaitAsync();
            try
            {
                string textToEmbed = $"{prop.Name}. {prop.Description}";
                if (string.IsNullOrWhiteSpace(textToEmbed)) return;

                if (!_embeddingCache.TryGetValue(textToEmbed, out var embedding))
                {
                    embedding = await _embeddingClient.GetEmbeddingAsync(textToEmbed);
                    _embeddingCache[textToEmbed] = embedding;
                }
                
                var metadata = JsonSerializer.Serialize(prop);
                lock (storage) // InMemoryVectorStorage Add is likely not thread-safe
                {
                    _vectorIndex.Add(embedding, metadata);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        // 3. Generate embedding for the query "vibe"
        var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(vibeDescription);

        // 4. Perform ultra-fast SIMD vector search
        var results = _vectorIndex.Search(queryEmbedding, topK);

        // 5. Map back to PropertyMatch objects
        var bestMatches = new List<PropertyMatch>();
        foreach (var res in results)
        {
            var prop = JsonSerializer.Deserialize<PropertyMatch>(res.Metadata);
            if (prop != null)
            {
                prop.VibeMatchScore = res.Score;
                bestMatches.Add(prop);
            }
        }

        return bestMatches;
    }

    /// <summary>
    /// Maps a collected Polaris DataFrame to a list of PropertyMatch objects.
    /// Centralises column extraction to avoid duplication.
    /// </summary>
    private static List<PropertyMatch> MapRows(dynamic df)
    {
        var matches = new List<PropertyMatch>();
        var idCol   = df.GetColumn("id");
        var nameCol = df.GetColumn("name");
        var descCol = df.GetColumn("description");
        var neighCol  = df.GetColumn("neighbourhood_cleansed");
        var priceCol  = df.GetColumn("price");
        var revCol    = df.GetColumn("number_of_reviews");
        var scoreCol  = df.GetColumn("review_scores_rating");

        for (int i = 0; i < df.RowCount; i++)
        {
            matches.Add(new PropertyMatch
            {
                Id           = idCol.Get(i)?.ToString() ?? "",
                Name         = nameCol.Get(i)?.ToString() ?? "",
                Description  = descCol.Get(i)?.ToString() ?? "",
                Neighbourhood = neighCol.Get(i)?.ToString() ?? "",
                Price        = priceCol.Get(i) is double p ? p : 0.0,
                ReviewScore  = scoreCol.Get(i) is double s ? s : 0.0,
                ReviewsCount = revCol.Get(i) is long r ? (int)r : 0
            });
        }
        return matches;
    }
}
