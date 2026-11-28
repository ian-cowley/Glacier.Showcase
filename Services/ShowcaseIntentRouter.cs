using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Glacier.Clavier.Engine;

namespace Glacier.Showcase.Services;

public enum ShowcaseSearchIntent
{
    FullProspectus = 0,
    BudgetFilter = 1,
    VibeSearch = 2,
    NeighborhoodAudit = 3
}

public sealed record ShowcaseRoutedIntent(
    ShowcaseSearchIntent Intent,
    float Confidence,
    double LatencyMs,
    ExtractedSearchParameters Parameters);

/// <summary>
/// Local System-1 intent classifier and parameter extractor powered by <see cref="ClavierSession"/>
/// and source-generated regular expressions, eliminating extra LLM extraction round-trips.
/// </summary>
public sealed partial class ShowcaseIntentRouter : IDisposable
{
    private static readonly string[] s_intentNames = Enum.GetNames<ShowcaseSearchIntent>();

    private static readonly (string Token, string CanonicalBorough)[] s_neighborhoodAliases =
    [
        ("Kensington and Chelsea", "Kensington and Chelsea"),
        ("Hammersmith and Fulham", "Hammersmith and Fulham"),
        ("City of London", "City of London"),
        ("Waltham Forest", "Waltham Forest"),
        ("Tower Hamlets", "Tower Hamlets"),
        ("Notting Hill", "Kensington and Chelsea"),
        ("Stoke Newington", "Hackney"),
        ("Bethnal Green", "Tower Hamlets"),
        ("Canary Wharf", "Tower Hamlets"),
        ("Shepherd's Bush", "Hammersmith and Fulham"),
        ("Walthamstow", "Waltham Forest"),
        ("Westminster", "Westminster"),
        ("Kensington", "Kensington and Chelsea"),
        ("Shoreditch", "Hackney"),
        ("Brick Lane", "Tower Hamlets"),
        ("Hammersmith", "Hammersmith and Fulham"),
        ("Islington", "Islington"),
        ("Southwark", "Southwark"),
        ("Bermondsey", "Southwark"),
        ("Greenwich", "Greenwich"),
        ("Wandsworth", "Wandsworth"),
        ("Battersea", "Wandsworth"),
        ("Twickenham", "Richmond"),
        ("Hampstead", "Barnet"),
        ("Wood Green", "Haringey"),
        ("West Ham", "Newham"),
        ("Stratford", "Newham"),
        ("Stockwell", "Lambeth"),
        ("Chelsea", "Kensington and Chelsea"),
        ("Hackney", "Hackney"),
        ("Dalston", "Hackney"),
        ("Camden", "Camden"),
        ("Lambeth", "Lambeth"),
        ("Brixton", "Lambeth"),
        ("Clapham", "Lambeth"),
        ("Fulham", "Hammersmith and Fulham"),
        ("Peckham", "Southwark"),
        ("Borough", "Southwark"),
        ("Lewisham", "Lewisham"),
        ("Brockley", "Lewisham"),
        ("Tooting", "Wandsworth"),
        ("Haringey", "Haringey"),
        ("Richmond", "Richmond"),
        ("Wembley", "Brent"),
        ("Poplar", "Tower Hamlets"),
        ("Newham", "Newham"),
        ("Barnet", "Barnet"),
        ("Brent", "Brent"),
        ("Soho", "Westminster"),
        ("Kew", "Richmond")
    ];

    private static readonly string[] s_knownVibeTerms =
    [
        "cozy", "modern", "minimal", "minimalist", "luxury", "boutique", "quirky",
        "quiet", "serene", "sunny", "bright", "loft", "cottage", "studio",
        "river", "riverside", "garden", "courtyard", "historic", "victorian",
        "artistic", "industrial", "chic", "family", "romantic", "vibrant",
        "nightlife", "peaceful", "penthouse", "stylish", "central"
    ];

    private readonly ClavierSession _clavier;
    private int _disposed;

    public ShowcaseIntentRouter()
    {
        _clavier = new ClavierSession(
            embeddingDim: 256,
            hiddenDim: 128,
            numActions: s_intentNames.Length,
            temperature: 0.85f);
    }

    public ShowcaseRoutedIntent Route(string prompt)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        string input = string.IsNullOrWhiteSpace(prompt) ? string.Empty : prompt.Trim();

        var sw = Stopwatch.StartNew();

        var decision = _clavier.DecideWithDistribution(input.AsSpan(), s_intentNames);
        var hasBudgetEmphasis = _clavier.VerifyWithDetails(
            input.AsSpan(),
            "Request specifies nightly budget price limit or review count constraints".AsSpan());
        var hasVibeEmphasis = _clavier.VerifyWithDetails(
            input.AsSpan(),
            "Request describes aesthetic atmosphere style or neighborhood vibe".AsSpan());

        var parameters = ExtractParametersCore(input);
        ShowcaseSearchIntent intent = ClassifyIntent(decision.ActionName, hasBudgetEmphasis.IsAffirmative, hasVibeEmphasis.IsAffirmative, parameters, input);

        sw.Stop();
        return new ShowcaseRoutedIntent(intent, decision.Decision.Confidence, sw.Elapsed.TotalMilliseconds, parameters);
    }

    public ExtractedSearchParameters ExtractParameters(string prompt)
    {
        return Route(prompt).Parameters;
    }

    private static ShowcaseSearchIntent ClassifyIntent(
        string? clavierAction,
        bool budgetSignal,
        bool vibeSignal,
        ExtractedSearchParameters p,
        string input)
    {
        if (input.Contains("prospectus", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("underwrite", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("investment", StringComparison.OrdinalIgnoreCase))
        {
            return ShowcaseSearchIntent.FullProspectus;
        }

        if (input.Contains("audit", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("safety", StringComparison.OrdinalIgnoreCase) ||
            input.Contains("transit", StringComparison.OrdinalIgnoreCase))
        {
            return ShowcaseSearchIntent.NeighborhoodAudit;
        }

        if (Enum.TryParse<ShowcaseSearchIntent>(clavierAction, out var parsed))
        {
            if (parsed == ShowcaseSearchIntent.BudgetFilter && !budgetSignal && vibeSignal)
            {
                return ShowcaseSearchIntent.VibeSearch;
            }
            return parsed;
        }

        return p.Neighborhood is not null && !budgetSignal ? ShowcaseSearchIntent.VibeSearch : ShowcaseSearchIntent.FullProspectus;
    }

    private static ExtractedSearchParameters ExtractParametersCore(string input)
    {
        double maxPrice = 150.0;
        int minReviews = 10;
        string? neighborhood = null;

        if (!string.IsNullOrWhiteSpace(input))
        {
            var priceMatch = PriceRegex().Match(input);
            if (priceMatch.Success)
            {
                string rawNum = priceMatch.Groups[1].Success
                    ? priceMatch.Groups[1].Value
                    : priceMatch.Groups[2].Value;
                if (double.TryParse(rawNum, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedPrice) && parsedPrice > 0)
                {
                    maxPrice = parsedPrice;
                }
            }

            var reviewsMatch = ReviewsRegex().Match(input);
            if (reviewsMatch.Success)
            {
                string rawReviews = reviewsMatch.Groups[1].Success
                    ? reviewsMatch.Groups[1].Value
                    : reviewsMatch.Groups[2].Value;
                if (int.TryParse(rawReviews, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedReviews) && parsedReviews >= 0)
                {
                    minReviews = parsedReviews;
                }
            }

            neighborhood = ExtractNeighborhood(input);
        }

        string vibe = ExtractVibe(input, neighborhood);

        return new ExtractedSearchParameters
        {
            MaxPrice = maxPrice,
            MinReviews = minReviews,
            Vibe = vibe,
            Neighborhood = neighborhood
        };
    }

    private static string? ExtractNeighborhood(string input)
    {
        foreach (var (token, canonical) in s_neighborhoodAliases)
        {
            if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        var inMatch = InNeighborhoodRegex().Match(input);
        if (inMatch.Success)
        {
            string candidate = inMatch.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ExtractVibe(string input, string? neighborhood)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "cozy";
        }

        var matchedVibes = new List<string>();
        foreach (string term in s_knownVibeTerms)
        {
            if (Regex.IsMatch(input, $@"\b{Regex.Escape(term)}\b", RegexOptions.IgnoreCase))
            {
                matchedVibes.Add(term);
            }
        }

        if (matchedVibes.Count > 0)
        {
            return string.Join(" ", matchedVibes);
        }

        string cleaned = PriceRegex().Replace(input, " ");
        cleaned = ReviewsRegex().Replace(cleaned, " ");
        if (!string.IsNullOrWhiteSpace(neighborhood))
        {
            cleaned = cleaned.Replace(neighborhood, " ", StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (token, _) in s_neighborhoodAliases)
        {
            cleaned = cleaned.Replace(token, " ", StringComparison.OrdinalIgnoreCase);
        }

        cleaned = FillerWordsRegex().Replace(cleaned, " ");
        cleaned = MultiWhitespaceRegex().Replace(cleaned, " ").Trim(' ', ',', '.', ';', ':', '?', '!');

        return string.IsNullOrWhiteSpace(cleaned) ? "cozy" : cleaned;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _clavier.Dispose();
        }
    }

    [GeneratedRegex(@"(?:under|below|max(?:imum)?|up\s+to|budget(?:\s+of)?|less\s+than|<=?)\s*[\$£€]?\s*(\d+(?:\.\d+)?)|[\$£€]\s*(\d+(?:\.\d+)?)(?:\s*(?:per\s+night|/nt|/night))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PriceRegex();

    [GeneratedRegex(@"(?:min(?:imum)?|at\s+least|>=)\s*(\d+)\s*(?:\+\s*)?(?:reviews?|ratings?)|(\d+)\s*\+\s*reviews?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReviewsRegex();

    [GeneratedRegex(@"\bin\s+([A-Z][a-z]+(?:\s+(?:and|of|[A-Z][a-z]+)){0,3})", RegexOptions.CultureInvariant)]
    private static partial Regex InNeighborhoodRegex();

    [GeneratedRegex(@"\b(?:find|me|a|an|the|for|with|and|in|at|near|around|stay|stays|property|properties|listing|listings|apartment|apartments|flat|flats|place|places|per|night|reviews?|under|below|max|please|show|looking|search)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FillerWordsRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespaceRegex();
}
