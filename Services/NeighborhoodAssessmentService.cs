using System;
using System.Collections.Generic;
using System.IO;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;

namespace Glacier.Showcase.Services;

public class NeighborhoodProfile
{
    public string Name { get; set; } = string.Empty;
    public string SafetyScore { get; set; } = string.Empty;
    public string Vibe { get; set; } = string.Empty;
    public string CrimeRate { get; set; } = string.Empty;
    public string Attractions { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public class TransitRoute
{
    public List<string> Path { get; set; } = new();
    public int Hops => Math.Max(0, Path.Count - 1);
}

public class NeighborhoodAssessmentService
{
    private readonly string _docTreeDbDir;
    private readonly string _graphDbPath;
    private readonly string _csrGraphPath;

    public NeighborhoodAssessmentService()
    {
        string baseDir = AppContext.BaseDirectory;
        _docTreeDbDir = Path.Combine(baseDir, "wwwroot", "doctree_db");
        _graphDbPath = Path.Combine(baseDir, "wwwroot", "graph_db", "london_transit.bin");
        _csrGraphPath = Path.Combine(baseDir, "wwwroot", "graph_db", "london_transit.csr");

        InitializeGraphAndDocTree();
    }

    public void InitializeGraphAndDocTree()
    {
        Directory.CreateDirectory(_docTreeDbDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_graphDbPath)!);

        // 1. Generate neighborhood transit connection graph
        if (!File.Exists(_csrGraphPath))
        {
            var store = new GraphStore(initialNodeCapacity: 50, initialEdgeCapacity: 200);

            // ── Core inner London ─────────────────────────────────────────────
            AddBidirectionalEdge(store, "Westminster",            "Kensington and Chelsea",   "TUBE");
            AddBidirectionalEdge(store, "Kensington and Chelsea", "Hammersmith and Fulham",   "TUBE");
            AddBidirectionalEdge(store, "Westminster",            "City of London",            "TUBE");
            AddBidirectionalEdge(store, "City of London",         "Tower Hamlets",             "TUBE");
            AddBidirectionalEdge(store, "Tower Hamlets",          "Hackney",                   "TUBE");
            AddBidirectionalEdge(store, "Hackney",                "Islington",                 "TUBE");
            AddBidirectionalEdge(store, "Islington",              "Camden",                    "TUBE");
            AddBidirectionalEdge(store, "Camden",                 "Westminster",               "TUBE");
            AddBidirectionalEdge(store, "Southwark",              "City of London",            "TUBE");
            AddBidirectionalEdge(store, "Southwark",              "Lambeth",                   "TUBE");
            AddBidirectionalEdge(store, "Lambeth",                "Westminster",               "TUBE");
            AddBidirectionalEdge(store, "Tower Hamlets",          "Southwark",                 "TUBE");

            // ── Outer rings ───────────────────────────────────────────────────
            AddBidirectionalEdge(store, "Wandsworth",             "Lambeth",                   "TUBE");
            AddBidirectionalEdge(store, "Wandsworth",             "Hammersmith and Fulham",    "TUBE");
            AddBidirectionalEdge(store, "Greenwich",              "Lewisham",                  "DLR");
            AddBidirectionalEdge(store, "Greenwich",              "Tower Hamlets",              "DLR");
            AddBidirectionalEdge(store, "Lewisham",               "Southwark",                 "OVERGROUND");
            AddBidirectionalEdge(store, "Newham",                 "Tower Hamlets",              "DLR");
            AddBidirectionalEdge(store, "Newham",                 "Hackney",                    "OVERGROUND");
            AddBidirectionalEdge(store, "Waltham Forest",         "Hackney",                    "OVERGROUND");
            AddBidirectionalEdge(store, "Haringey",               "Islington",                  "TUBE");
            AddBidirectionalEdge(store, "Haringey",               "Camden",                     "TUBE");
            AddBidirectionalEdge(store, "Barnet",                 "Camden",                     "TUBE");
            AddBidirectionalEdge(store, "Brent",                  "Camden",                     "TUBE");
            AddBidirectionalEdge(store, "Brent",                  "Westminster",                "TUBE");
            AddBidirectionalEdge(store, "Richmond",               "Hammersmith and Fulham",    "TUBE");
            AddBidirectionalEdge(store, "Richmond",               "Wandsworth",                 "OVERGROUND");

            store.SaveToDisk(_graphDbPath);
            CsrGraphCompiler.CompileFromForwardStar(store, _csrGraphPath);
        }

        // 2. Generate DocTree neighborhood profile database
        string rootFilePath = Path.Combine(_docTreeDbDir, "root.json");
        if (!File.Exists(rootFilePath))
        {
            var root = new DocNode { Type = NodeType.Root, Content = "London Neighborhood Profiles" };

            AddNeighborhoodDoc(root, "Westminster",            "High-end historical and cultural core of London. Features historic architecture, monuments, and premium shopping areas.",               "9/10",   "Historical & Busy",           "Low",         "Big Ben, Westminster Abbey, Parliament, Buckingham Palace");
            AddNeighborhoodDoc(root, "Camden",                 "Famous for its alternative music scene, quirky street fashion, and massive outdoor markets. Artistic, energetic, and highly dynamic.",    "7/10",   "Alternative & Live Music",    "Medium",      "Camden Market, Regent's Park, Roundhouse Theatre");
            AddNeighborhoodDoc(root, "Kensington and Chelsea", "Affluent residential neighborhood, beautifully quiet and lined with stucco houses, garden squares, and elegant high-street shops.",        "9.5/10", "Affluent & Serene",           "Very Low",    "Natural History Museum, Hyde Park, Science Museum");
            AddNeighborhoodDoc(root, "Hackney",                "A trendy creative district packed with coffee roasters, independent breweries, street murals, and artisan craft stores.",                   "8/10",   "Hipster & Gastronomic",       "Medium-Low",  "London Fields, Broadway Market, Victoria Park");
            AddNeighborhoodDoc(root, "Tower Hamlets",          "Vibrant, historic East End neighborhood with diverse cultures, modern riverside skyscrapers, and busy street markets.",                     "7.5/10", "Diverse & Bustling",          "Medium",      "Tower of London, Brick Lane, Canary Wharf");
            AddNeighborhoodDoc(root, "Islington",              "Sophisticated residential neighborhood boasting leafy streets, trendy pubs, boutique theaters, and upscale dining options.",                 "8.5/10", "Chic & Villagey",             "Low",         "Sadler's Wells Theatre, Upper Street Shopping, Union Chapel");
            AddNeighborhoodDoc(root, "Southwark",              "A dynamic cultural hub along the southern bank of the Thames, showcasing modern art galleries, iconic food markets, and architectural landmarks.", "8/10", "Modern & Gastronomic",       "Medium-Low",  "Tate Modern, Borough Market, The Shard, Globe Theatre");
            AddNeighborhoodDoc(root, "Lambeth",                "Energetic riverside district hosting street buskers, pop-up container food malls, skate parks, and central railway terminals.",             "7.5/10", "Artistic & Lively",           "Medium",      "London Eye, Southbank Centre, National Theatre, Brixton Market");
            AddNeighborhoodDoc(root, "Hammersmith and Fulham", "Charming riverside neighborhood featuring leafy family properties, traditional English gastropubs, and excellent waterside trails.",          "8.5/10", "Riverside & Residential",     "Low",         "Fulham Palace, Thames Path, Lyric Hammersmith");
            AddNeighborhoodDoc(root, "City of London",         "The financial hub and oldest square mile of London, blending medieval history with futuristic architectural masterpieces.",                  "9/10",   "Professional & Ancient",      "Very Low",    "St Paul's Cathedral, Tower Bridge, Museum of London");

            // ── Outer boroughs ────────────────────────────────────────────────
            AddNeighborhoodDoc(root, "Wandsworth",             "A leafy, family-friendly south-west London borough with excellent riverside walks, independent restaurants, and some of the best parks in London.",  "8.5/10", "Suburban & Green",           "Low",         "Wandsworth Common, Clapham Junction, Battersea Power Station");
            AddNeighborhoodDoc(root, "Greenwich",              "Steeped in maritime heritage and royal history. Home to the Prime Meridian, a UNESCO World Heritage Site, and outstanding riverside pubs.",         "8/10",   "Historic & Scenic",          "Low",         "Cutty Sark, Greenwich Park, Royal Observatory, National Maritime Museum");
            AddNeighborhoodDoc(root, "Lewisham",               "A fast-gentrifying south-east borough with excellent markets, independent cafes, and direct DLR links to Canary Wharf and the City.",              "7/10",   "Creative & Up-and-Coming",   "Medium",      "Lewisham Market, Hilly Fields Park, Brockley Cemetery");
            AddNeighborhoodDoc(root, "Newham",                 "A highly diverse, rapidly developing east London borough anchored by the Olympic Park, London Stadium, and Westfield Stratford City.",             "7/10",   "Dynamic & Regenerating",     "Medium",      "Olympic Park, London Stadium, Westfield Stratford, ExCel London");
            AddNeighborhoodDoc(root, "Waltham Forest",         "A proudly independent and creative north-east London borough, designated London Borough of Culture 2019. Fantastic parks and cycling routes.",    "7.5/10", "Independent & Cultural",     "Medium-Low",  "Epping Forest, Walthamstow Market, Walthamstow Wetlands");
            AddNeighborhoodDoc(root, "Haringey",               "A diverse north London borough encompassing the affluent Muswell Hill and the vibrant Wood Green retail district.",                                "7/10",   "Diverse & Multi-Cultural",   "Medium",      "Alexandra Palace, Finsbury Park, Wood Green Shopping City");
            AddNeighborhoodDoc(root, "Barnet",                 "A leafy outer north London borough valued for its outstanding schools, spacious properties, and proximity to the countryside.",                     "8.5/10", "Suburban & Family",          "Very Low",    "Hampstead Heath, RAF Museum, Whalebones Park, Barnet Market");
            AddNeighborhoodDoc(root, "Brent",                  "Home to Wembley Stadium and a rich mosaic of cultures including the famous Little India of Southall. Improving infrastructure and connectivity.",   "7/10",   "Culturally Diverse",         "Medium",      "Wembley Stadium, Wembley Arena, Brent Reservoir, Neasden Temple");
            AddNeighborhoodDoc(root, "Richmond",               "One of London's most desirable boroughs — Richmond Park, the Thames towpath, and outstanding Georgian architecture alongside rural charm.",        "9.5/10", "Affluent & Rural",           "Very Low",    "Richmond Park, Kew Gardens, Ham House, Twickenham Stadium");

            TreeSerializer.Serialize(root, _docTreeDbDir);
        }
    }

    private void AddBidirectionalEdge(GraphStore store, string from, string to, string rel)
    {
        store.AddEdge(from, to, rel);
        store.AddEdge(to, from, rel);
    }

    private void AddNeighborhoodDoc(DocNode root, string name, string desc, string safety, string vibe, string crime, string attractions)
    {
        var node = new DocNode
        {
            Type = NodeType.Paragraph,
            Content = desc,
            Metadata = new Dictionary<string, string>
            {
                { "Name", name },
                { "SafetyScore", safety },
                { "Vibe", vibe },
                { "CrimeRate", crime },
                { "Attractions", attractions }
            }
        };
        root.AddChild(node);
    }

    /// <summary>
    /// Finds the shortest transit route from a borough to London Central (City of London) using out-of-core CSR Graph.
    /// Uses a small memory constraint to demonstrate page-cached disk traversal.
    /// </summary>
    public TransitRoute GetTransitRouteToCentral(string neighborhood)
    {
        var result = new TransitRoute();
        try
        {
            // Normalize neighborhood naming
            string normalizedName = GetNormalizedNeighborhood(neighborhood);

            // Load CSR graph with a constrained page cache (256KB)
            using var csrStore = new CsrGraphStore(_csrGraphPath, maxMemoryBytes: 256 * 1024);
            var search = new CsrGraphSearch(csrStore);

            var path = search.FindShortestPath(normalizedName, "City of London");
            if (path != null && path.Count > 0)
            {
                result.Path = path;
            }
            else
            {
                // Fallback direct hop
                result.Path = new List<string> { normalizedName, "City of London" };
            }
        }
        catch (Exception)
        {
            // Fallback direct path
            result.Path = new List<string> { neighborhood, "City of London" };
        }
        return result;
    }

    /// <summary>
    /// Lazily reads the neighborhood document details from the serialized DocTree on disk.
    /// </summary>
    public NeighborhoodProfile GetNeighborhoodProfile(string neighborhood)
    {
        string normalizedName = GetNormalizedNeighborhood(neighborhood);
        
        try
        {
            // Load the root of the tree lazily (proxy object)
            var root = TreeSerializer.DeserializeLazy(_docTreeDbDir);

            // Traverse the children (which are deserialized from disk on-demand as we look at their properties)
            foreach (var child in root.Children)
            {
                if (child.Metadata.TryGetValue("Name", out var name) && 
                    string.Equals(name, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return new NeighborhoodProfile
                    {
                        Name = name,
                        Description = child.Content,
                        SafetyScore = child.Metadata.GetValueOrDefault("SafetyScore", "8/10"),
                        Vibe = child.Metadata.GetValueOrDefault("Vibe", "Vibrant"),
                        CrimeRate = child.Metadata.GetValueOrDefault("CrimeRate", "Low"),
                        Attractions = child.Metadata.GetValueOrDefault("Attractions", "Local amenities")
                    };
                }
            }
        }
        catch (Exception)
        {
            // Fall through to default
        }

        // Return a mock default if not found
        return new NeighborhoodProfile
        {
            Name = normalizedName,
            Description = "A lovely residential area in London with local stores and parks.",
            SafetyScore = "8/10",
            Vibe = "Cosmopolitan & Green",
            CrimeRate = "Low",
            Attractions = "Local cafes, transit stations"
        };
    }

    private string GetNormalizedNeighborhood(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "City of London";

        // ── Inner London ──────────────────────────────────────────────────────
        if (input.Contains("Westminster",          StringComparison.OrdinalIgnoreCase)) return "Westminster";
        if (input.Contains("Notting Hill",          StringComparison.OrdinalIgnoreCase)) return "Kensington and Chelsea";
        if (input.Contains("Kensington",            StringComparison.OrdinalIgnoreCase)) return "Kensington and Chelsea";
        if (input.Contains("Chelsea",               StringComparison.OrdinalIgnoreCase)) return "Kensington and Chelsea";
        if (input.Contains("Camden",                StringComparison.OrdinalIgnoreCase)) return "Camden";
        if (input.Contains("Islington",             StringComparison.OrdinalIgnoreCase)) return "Islington";
        if (input.Contains("Shoreditch",            StringComparison.OrdinalIgnoreCase)) return "Hackney";
        if (input.Contains("Dalston",               StringComparison.OrdinalIgnoreCase)) return "Hackney";
        if (input.Contains("Stoke Newington",       StringComparison.OrdinalIgnoreCase)) return "Hackney";
        if (input.Contains("Hackney",               StringComparison.OrdinalIgnoreCase)) return "Hackney";
        if (input.Contains("Bethnal Green",         StringComparison.OrdinalIgnoreCase)) return "Tower Hamlets";
        if (input.Contains("Canary Wharf",          StringComparison.OrdinalIgnoreCase)) return "Tower Hamlets";
        if (input.Contains("Poplar",                StringComparison.OrdinalIgnoreCase)) return "Tower Hamlets";
        if (input.Contains("Brick Lane",            StringComparison.OrdinalIgnoreCase)) return "Tower Hamlets";
        if (input.Contains("Tower Hamlets",         StringComparison.OrdinalIgnoreCase)) return "Tower Hamlets";
        if (input.Contains("Borough",              StringComparison.OrdinalIgnoreCase)) return "Southwark";
        if (input.Contains("Peckham",              StringComparison.OrdinalIgnoreCase)) return "Southwark";
        if (input.Contains("Bermondsey",           StringComparison.OrdinalIgnoreCase)) return "Southwark";
        if (input.Contains("Southwark",            StringComparison.OrdinalIgnoreCase)) return "Southwark";
        if (input.Contains("Brixton",              StringComparison.OrdinalIgnoreCase)) return "Lambeth";
        if (input.Contains("Stockwell",            StringComparison.OrdinalIgnoreCase)) return "Lambeth";
        if (input.Contains("Clapham",              StringComparison.OrdinalIgnoreCase)) return "Lambeth";
        if (input.Contains("Lambeth",              StringComparison.OrdinalIgnoreCase)) return "Lambeth";
        if (input.Contains("Hammersmith",          StringComparison.OrdinalIgnoreCase)) return "Hammersmith and Fulham";
        if (input.Contains("Fulham",               StringComparison.OrdinalIgnoreCase)) return "Hammersmith and Fulham";
        if (input.Contains("Shepherd's Bush",      StringComparison.OrdinalIgnoreCase)) return "Hammersmith and Fulham";
        if (input.Contains("City of London",       StringComparison.OrdinalIgnoreCase)) return "City of London";

        // ── Outer London ──────────────────────────────────────────────────────
        if (input.Contains("Battersea",            StringComparison.OrdinalIgnoreCase)) return "Wandsworth";
        if (input.Contains("Tooting",              StringComparison.OrdinalIgnoreCase)) return "Wandsworth";
        if (input.Contains("Wandsworth",           StringComparison.OrdinalIgnoreCase)) return "Wandsworth";
        if (input.Contains("Greenwich",            StringComparison.OrdinalIgnoreCase)) return "Greenwich";
        if (input.Contains("Lewisham",             StringComparison.OrdinalIgnoreCase)) return "Lewisham";
        if (input.Contains("Brockley",             StringComparison.OrdinalIgnoreCase)) return "Lewisham";
        if (input.Contains("Stratford",            StringComparison.OrdinalIgnoreCase)) return "Newham";
        if (input.Contains("West Ham",             StringComparison.OrdinalIgnoreCase)) return "Newham";
        if (input.Contains("Newham",               StringComparison.OrdinalIgnoreCase)) return "Newham";
        if (input.Contains("Walthamstow",          StringComparison.OrdinalIgnoreCase)) return "Waltham Forest";
        if (input.Contains("Waltham Forest",       StringComparison.OrdinalIgnoreCase)) return "Waltham Forest";
        if (input.Contains("Haringey",             StringComparison.OrdinalIgnoreCase)) return "Haringey";
        if (input.Contains("Wood Green",           StringComparison.OrdinalIgnoreCase)) return "Haringey";
        if (input.Contains("Barnet",               StringComparison.OrdinalIgnoreCase)) return "Barnet";
        if (input.Contains("Hampstead",            StringComparison.OrdinalIgnoreCase)) return "Barnet";
        if (input.Contains("Wembley",              StringComparison.OrdinalIgnoreCase)) return "Brent";
        if (input.Contains("Brent",                StringComparison.OrdinalIgnoreCase)) return "Brent";
        if (input.Contains("Richmond",             StringComparison.OrdinalIgnoreCase)) return "Richmond";
        if (input.Contains("Twickenham",           StringComparison.OrdinalIgnoreCase)) return "Richmond";
        if (input.Contains("Kew",                  StringComparison.OrdinalIgnoreCase)) return "Richmond";

        // Fallback
        return "City of London";
    }
}
