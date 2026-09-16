using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Glacier.AgentDevKit.Adk;

namespace Glacier.Showcase.Services;

public class TelemetryAgent : LlmAgent
{
    private readonly AgentOrchestratorState _state;
    private readonly string _customName;

    public TelemetryAgent(string name, string model, string description, string instruction, List<ITool> tools, AgentOrchestratorState state)
        : base(name, model, description, instruction, tools)
    {
        _customName = name;
        _state = state;
    }

    public override Task OnBeforeModelInvokeAsync(LlmRequest request)
    {
        _state.SetActiveAgent(_customName);
        _state.SetAgentStatus(_customName, "Thinking");
        _state.AddStepLog(_customName, "Model Input", $"Generating query token targeting {Model} model...", SerializeContents(request.Contents));
        return Task.CompletedTask;
    }

    public override Task OnAfterModelInvokeAsync(LlmResponse response)
    {
        _state.SetAgentStatus(_customName, "Idle");
        if (response.FunctionCalls?.Any() == true)
        {
            foreach (var call in response.FunctionCalls)
            {
                _state.AddStepLog(_customName, "Model Output (Function Call)", $"LLM requested tool invocation: {call.Name}", call.Args?.ToJsonString() ?? "");
            }
        }
        else
        {
            _state.AddStepLog(_customName, "Model Output (Response)", response.Content ?? "", response.Content ?? "");
        }
        return Task.CompletedTask;
    }

    public override Task OnBeforeToolInvokeAsync(ITool tool, string arguments)
    {
        _state.SetActiveAgent(_customName);
        _state.SetAgentStatus(_customName, "Executing Tool");
        _state.AddStepLog(_customName, "Tool Call", $"Invoking tool '{tool.Name}' with parameters.", arguments);
        return Task.CompletedTask;
    }

    public override Task OnAfterToolInvokeAsync(ITool tool, string result)
    {
        _state.SetAgentStatus(_customName, "Idle");
        _state.AddStepLog(_customName, "Tool Return", $"Tool '{tool.Name}' completed execution successfully.", result);
        return Task.CompletedTask;
    }

    private string SerializeContents(List<LlmContent> contents)
    {
        try
        {
            return JsonSerializer.Serialize(contents, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return "[]";
        }
    }
}

public class FilterPropertiesTool : ITool
{
    private readonly AirbnbDataService _dataService;
    private readonly AgentOrchestratorState _state;
    public string Name => "FilterProperties";
    public string Description => "Filters the Airbnb dataset by maximum price and minimum reviews. Use this first to narrow down the dataset.";

    public FilterPropertiesTool(AirbnbDataService dataService, AgentOrchestratorState state)
    {
        _dataService = dataService;
        _state = state;
    }

    public JsonNode GetParametersSchema()
    {
        return JsonNode.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""maxPrice"": { ""type"": ""number"", ""description"": ""Maximum price per night."" },
                ""minReviews"": { ""type"": ""integer"", ""description"": ""Minimum number of reviews required."" },
                ""neighborhood"": { ""type"": ""string"", ""description"": ""Optional neighborhood to filter by."" }
            },
            ""required"": [""maxPrice"", ""minReviews""]
        }")!;
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            double maxPrice = args.GetProperty("maxPrice").GetDouble();
            int minReviews = args.GetProperty("minReviews").GetInt32();
            string neighborhood = args.TryGetProperty("neighborhood", out var nProp) ? nProp.GetString() ?? "" : "";
            
            _state.SetExecutionPhase("Filtering");
            var matches = await _dataService.FilterPropertiesAsync(maxPrice, minReviews, neighborhood);
            _state.FilteredProperties = matches;
            
            return $"Successfully filtered properties using Glacier.Polaris. Found {matches.Count} candidate properties.";
        }
        catch (Exception ex) 
        { 
            return $"Error during filtration: {ex.Message}"; 
        }
    }
}

public class SemanticSearchTool : ITool
{
    private readonly AirbnbDataService _dataService;
    private readonly AgentOrchestratorState _state;
    public string Name => "SemanticSearch";
    public string Description => "Performs semantic vector search on the filtered properties to find the best match for a specific vibe or description.";

    public SemanticSearchTool(AirbnbDataService dataService, AgentOrchestratorState state)
    {
        _dataService = dataService;
        _state = state;
    }

    public JsonNode GetParametersSchema()
    {
        return JsonNode.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""vibe"": { ""type"": ""string"", ""description"": ""The vibe or description to search for (e.g. 'modern minimal', 'cozy cottage')."" },
                ""neighborhood"": { ""type"": ""string"", ""description"": ""Optional neighborhood to search within."" }
            },
            ""required"": [""vibe""]
        }")!;
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            string vibe = args.GetProperty("vibe").GetString() ?? "";
            string neighborhood = args.TryGetProperty("neighborhood", out var nProp) ? nProp.GetString() ?? "" : "";

            _state.SetExecutionPhase("Vibe Search");
            
            List<PropertyMatch> candidates;
            if (!string.IsNullOrEmpty(neighborhood))
            {
                candidates = await _dataService.GetPropertiesInNeighborhoodAsync(neighborhood);
            }
            else
            {
                candidates = _state.FilteredProperties.Count > 0 
                    ? _state.FilteredProperties 
                    : await _dataService.GetPropertiesInNeighborhoodAsync("Westminster"); // fallback
            }

            var finalMatches = await _dataService.SemanticSearchAsync(candidates, vibe);
            _state.FinalMatches = finalMatches;
            
            var summary = $"Vector search complete! Found {finalMatches.Count} matches matching the vibe '{vibe}'.\n";
            foreach (var m in finalMatches.Take(3)) 
                summary += $"- {m.Name} in {m.Neighbourhood} (Price: ${m.Price}, Vibe Score: {m.VibeMatchScore:F2})\n";
            
            return summary;
        }
        catch (Exception ex) 
        { 
            return $"Error during semantic search: {ex.Message}"; 
        }
    }
}

public class AuditNeighborhoodTool : ITool
{
    private readonly NeighborhoodAssessmentService _assessmentService;
    private readonly AgentOrchestratorState _state;
    public string Name => "AuditNeighborhood";
    public string Description => "Audits neighborhood attributes (safety, vibe, crime rate, attractions) and transit routes to central London (City of London) using out-of-core CSR Graphs and DocTrees.";

    public AuditNeighborhoodTool(NeighborhoodAssessmentService assessmentService, AgentOrchestratorState state)
    {
        _assessmentService = assessmentService;
        _state = state;
    }

    public JsonNode GetParametersSchema()
    {
        return JsonNode.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""neighborhood"": { ""type"": ""string"", ""description"": ""The name of the neighborhood / borough (e.g. 'Westminster')."" }
            },
            ""required"": [""neighborhood""]
        }")!;
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            string neighborhood = args.GetProperty("neighborhood").GetString() ?? "";

            _state.SetExecutionPhase("Auditing Neighborhood");
            var profile = _assessmentService.GetNeighborhoodProfile(neighborhood);
            var route = _assessmentService.GetTransitRouteToCentral(neighborhood);

            _state.ActiveNeighborhoodProfile = profile;
            _state.ActiveTransitRoute = route;

            var resultObj = new { Profile = profile, Route = route };
            return JsonSerializer.Serialize(resultObj, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) 
        { 
            return $"Error auditing neighborhood: {ex.Message}"; 
        }
    }
}

public class GenerateReportTool : ITool
{
    private readonly PdfReportService _pdfService;
    private readonly AgentOrchestratorState _state;
    public string Name => "GenerateReport";
    public string Description => "Generates a professional PDF Investment Prospectus for the final matched properties, combining property lists with neighborhood transit risk scores.";

    public GenerateReportTool(PdfReportService pdfService, AgentOrchestratorState state)
    {
        _pdfService = pdfService;
        _state = state;
    }

    public JsonNode GetParametersSchema()
    {
        return JsonNode.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""rationale"": { ""type"": ""string"", ""description"": ""The investment rationale to include in the report."" }
            },
            ""required"": [""rationale""]
        }")!;
    }

    public async Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            var args = JsonDocument.Parse(arguments).RootElement;
            string rationale = args.GetProperty("rationale").GetString() ?? "";

            if (_state.FinalMatches.Count == 0) 
            {
                return "Error: No final matches. You must call SemanticSearch first.";
            }
            
            _state.SetExecutionPhase("Designing Prospectus");
            string pdfUrl = _pdfService.GenerateInvestmentProspectus(
                _state.FinalMatches, 
                rationale, 
                _state.ActiveTransitRoute, 
                _state.ActiveNeighborhoodProfile,
                _state.UnderwrittenProperties.Count > 0 ? _state.UnderwrittenProperties : null
            );
            _state.ProspectusPdfPath = pdfUrl;
            
            return $"SUCCESS: PDF Prospectus generated successfully. Direct Link: {pdfUrl}";
        }
        catch (Exception ex) 
        { 
            return $"Error generating report: {ex.Message}"; 
        }
    }
}

public class UnderwritePropertiesTool : ITool
{
    private readonly AgentOrchestratorState _state;
    public string Name => "UnderwriteProperties";
    public string Description => "Underwrites property listings by calculating occupancy rates, gross annual revenue, operating expenses, net operating income (NOI), cap rate / rental yield, and cash-on-cash return.";

    public UnderwritePropertiesTool(AgentOrchestratorState state)
    {
        _state = state;
    }

    public JsonNode GetParametersSchema()
    {
        return JsonNode.Parse(@"{
            ""type"": ""object"",
            ""properties"": {}
        }")!;
    }

    public Task<string> ExecuteAsync(string arguments)
    {
        try
        {
            _state.SetExecutionPhase("Underwriting Projections");
            _state.UnderwrittenProperties.Clear();

            int hops = _state.ActiveTransitRoute?.Hops ?? 2;
            
            // Underwrite top 3 collated property matches
            foreach (var prop in _state.FinalMatches.Take(3))
            {
                double price = prop.Price > 0 ? prop.Price : 120.0;
                double occupancyRate = Math.Clamp(0.85 - 0.03 * hops, 0.55, 0.90);
                double grossRevenue = price * 365.0 * occupancyRate;
                double operatingExpenses = grossRevenue * 0.35 + 3000.0;
                double netIncome = grossRevenue - operatingExpenses;
                
                // Estimate property purchase value (Westminster multiplier is higher, baseline 2800)
                double multiplier = prop.Neighbourhood.Contains("Westminster", StringComparison.OrdinalIgnoreCase) ? 3200.0 : 2600.0;
                double estValue = Math.Max(150000.0, price * multiplier);
                double yield = netIncome / estValue;
                
                // Cash invested: 30% of valuation (25% downpayment + 5% fees)
                double cashInvested = estValue * 0.30;
                // Annual Interest Payment (75% LTV at 5.5% interest)
                double annualMortgage = (estValue * 0.75) * 0.055;
                double cashFlow = netIncome - annualMortgage;
                double cashOnCash = cashFlow / cashInvested;

                var uw = new PropertyUnderwriting
                {
                    PropertyId = prop.Id,
                    PropertyName = prop.Name,
                    EstimatedMonthlyRent = grossRevenue / 12.0,
                    OccupancyRate = occupancyRate,
                    GrossAnnualRevenue = grossRevenue,
                    OperatingExpenses = operatingExpenses,
                    NetAnnualIncome = netIncome,
                    EstimatedValue = estValue,
                    RentalYield = yield,
                    CashOnCashReturn = cashOnCash
                };

                _state.UnderwrittenProperties.Add(uw);
            }

            var summary = $"Underwriting complete. Evaluated {_state.UnderwrittenProperties.Count} properties:\n";
            foreach (var uw in _state.UnderwrittenProperties)
            {
                summary += $"- {uw.PropertyName}: Valued at £{uw.EstimatedValue:N0}. Est Occupancy: {(uw.OccupancyRate * 100):F0}%. Gross Rev: £{uw.GrossAnnualRevenue:N0}/yr. Net Income: £{uw.NetAnnualIncome:N0}/yr. Cap Rate (Yield): {(uw.RentalYield * 100):F1}%. Cash-on-Cash: {(uw.CashOnCashReturn * 100):F1}%.\n";
            }
            return Task.FromResult(summary);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error during underwriting: {ex.Message}");
        }
    }
}

public class DelegateTool : ITool
{
    private readonly TelemetryAgent _specialistAgent;
    private readonly ILlmService _llmService;
    private readonly string _specialistName;
    private readonly string _description;
    private readonly JsonNode _schema;

    public DelegateTool(string name, string description, TelemetryAgent specialistAgent, ILlmService llmService, string schemaJson)
    {
        Name = name;
        _description = description;
        _specialistAgent = specialistAgent;
        _llmService = llmService;
        _schema = JsonNode.Parse(schemaJson)!;
        _specialistName = specialistAgent.Name;
    }

    public string Name { get; }
    public string Description => _description;
    public JsonNode GetParametersSchema() => _schema;

    public async Task<string> ExecuteAsync(string arguments)
    {
        // Synthesize an instructional prompt containing the arguments for the specialist
        var prompt = $"Task Assignment: Execute your specialty task with parameters: {arguments}. Run your tool and return the exact text results.";
        var result = await _specialistAgent.RunAsync(prompt, _llmService);
        return result;
    }
}

public class AgentService
{
    private readonly ILlmService _llmService;
    
    // Core Agents
    public TelemetryAgent AcquisitionManager { get; }
    public TelemetryAgent PolarisAnalyst { get; }
    public TelemetryAgent VectorSpecialist { get; }
    public TelemetryAgent RiskAuditor { get; }
    public TelemetryAgent ProspectusDesigner { get; }
    public TelemetryAgent FinancialUnderwriter { get; }

    public AgentOrchestratorState State { get; }

    public AgentService(
        AirbnbDataService dataService, 
        NeighborhoodAssessmentService assessmentService,
        PdfReportService pdfService, 
        AgentOrchestratorState state,
        IConfiguration config)
    {
        State = state;
        string apiKey = config["GEMINI_API_KEY"] ?? config["GeminiApiKey"] ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
        _llmService = !string.IsNullOrEmpty(apiKey) ? new GeminiService(apiKey) : null!;

        // Instantiate Specialist Agents
        PolarisAnalyst = new TelemetryAgent(
            name: "Polaris Analyst",
            model: "gemini-2.5-flash",
            description: "Filters property datasets in-memory using Polaris expression trees.",
            instruction: "You are the Polaris Analyst. Your only job is to execute the FilterProperties tool using the maxPrice and minReviews parameters passed to you. Return the exact response text of the tool.",
            tools: new List<ITool> { new FilterPropertiesTool(dataService, State) },
            state: State
        );

        VectorSpecialist = new TelemetryAgent(
            name: "Vector Specialist",
            model: "gemini-2.5-flash",
            description: "Computes and index high-dimensional embeddings for cosine similarity matchings.",
            instruction: "You are the Vector Specialist. Your only job is to execute the SemanticSearch tool using the vibe parameters passed to you. Return the exact response text of the tool.",
            tools: new List<ITool> { new SemanticSearchTool(dataService, State) },
            state: State
        );

        RiskAuditor = new TelemetryAgent(
            name: "Risk Auditor",
            model: "gemini-2.5-flash",
            description: "Audits safety ratings and graphs transit routes to central London.",
            instruction: "You are the Risk Auditor. Your only job is to execute the AuditNeighborhood tool using the neighborhood parameter passed to you. Return the exact response text of the tool.",
            tools: new List<ITool> { new AuditNeighborhoodTool(assessmentService, State) },
            state: State
        );

        ProspectusDesigner = new TelemetryAgent(
            name: "Prospectus Designer",
            model: "gemini-2.5-flash",
            description: "Compiles final investment rationale and exports high-fidelity PDFs.",
            instruction: "You are the Prospectus Designer. Your only job is to execute the GenerateReport tool using the rationale parameter passed to you. Return the exact response text of the tool.",
            tools: new List<ITool> { new GenerateReportTool(pdfService, State) },
            state: State
        );

        FinancialUnderwriter = new TelemetryAgent(
            name: "Financial Underwriter",
            model: "gemini-2.5-flash",
            description: "Calculates property metrics, yields, and cash-on-cash projections.",
            instruction: "You are the Financial Underwriter. Your only job is to execute the UnderwriteProperties tool. Return the exact response text of the tool.",
            tools: new List<ITool> { new UnderwritePropertiesTool(State) },
            state: State
        );

        // Instantiate Acquisition Manager (Lead Coordinator)
        var acquisitionTools = new List<ITool>
        {
            new DelegateTool(
                name: "DelegateToPolarisAnalyst",
                description: "Delegates to the Polaris Analyst to filter property listings by price and reviews. Call this first.",
                specialistAgent: PolarisAnalyst,
                llmService: _llmService,
                schemaJson: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""maxPrice"": { ""type"": ""number"", ""description"": ""Maximum price per night."" },
                        ""minReviews"": { ""type"": ""integer"", ""description"": ""Minimum reviews count."" }
                    },
                    ""required"": [""maxPrice"", ""minReviews""]
                }"
            ),
            new DelegateTool(
                name: "DelegateToVectorSpecialist",
                description: "Delegates to the Vector Specialist to search the filtered candidates semantically for a specific vibe. Call this second.",
                specialistAgent: VectorSpecialist,
                llmService: _llmService,
                schemaJson: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""vibe"": { ""type"": ""string"", ""description"": ""Descriptive vibe (e.g. cozy luxury, modern minimal)."" }
                    },
                    ""required"": [""vibe""]
                }"
            ),
            new DelegateTool(
                name: "DelegateToRiskAuditor",
                description: "Delegates to the Risk Auditor to retrieve safety scores and transit paths for a neighborhood. Call this third, using the neighborhood from the top listing.",
                specialistAgent: RiskAuditor,
                llmService: _llmService,
                schemaJson: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""neighborhood"": { ""type"": ""string"", ""description"": ""Borough name to audit (e.g. Westminster)."" }
                    },
                    ""required"": [""neighborhood""]
                }"
            ),
            new DelegateTool(
                name: "DelegateToProspectusDesigner",
                description: "Delegates to the Prospectus Designer to generate a PDF Investment Prospectus with your analysis rationale. Call this last.",
                specialistAgent: ProspectusDesigner,
                llmService: _llmService,
                schemaJson: @"{
                    ""type"": ""object"",
                    ""properties"": {
                        ""rationale"": { ""type"": ""string"", ""description"": ""Detailed investment rationale text explaining why these listings match the request."" }
                    },
                    ""required"": [""rationale""]
                }"
            )
        };

        AcquisitionManager = new TelemetryAgent(
            name: "Acquisition Manager",
            model: "gemini-2.5-flash",
            description: "Directs client requests and synthesizes team specialist outputs.",
            instruction: @"You are the Acquisition Manager. Your goal is to coordinate a team of specialized agents to find the best properties and compile a comprehensive prospectus.
You have the following specialists:
1. Polaris Analyst (via DelegateToPolarisAnalyst tool): Filters listings by max price and min reviews.
2. Vector Specialist (via DelegateToVectorSpecialist tool): Performs semantic search for a specific vibe on the filtered listings.
3. Risk Auditor (via DelegateToRiskAuditor tool): Audits safety, vibe, crime, and transit routes for the target neighborhood.
4. Prospectus Designer (via DelegateToProspectusDesigner tool): Generates a high-quality PDF prospectus containing the final matches, investment rationale, transit route, and neighborhood safety profile.

Workflow:
1. First, call DelegateToPolarisAnalyst to filter the listings by max price and min reviews.
2. Second, call DelegateToVectorSpecialist to find the listings matching the client's desired vibe.
3. Third, inspect the top matches returned. Extract the neighborhood of the best match and call DelegateToRiskAuditor for that neighborhood.
4. Fourth, write a solid, professional investment rationale summarizing the safety score, transit hops, and vibe matching, and call DelegateToProspectusDesigner to compile the final prospectus.
5. Finally, present the prospectus download link and a high-level summary of the results to the user.

IMPORTANT: Do not skip steps. Do not hallucinate links or data. Always call the tools in sequence.",
            tools: acquisitionTools,
            state: State
        );
    }

    public async Task<string> RunAsync(string prompt)
    {
        State.Reset();
        State.AddChatMessage("Client", prompt);

        try
        {
            // 1. Parameter Extraction
            var extractionRequest = new LlmRequest
            {
                Model = "models/gemini-2.5-flash",
                SystemInstruction = LlmContent.Model("You are a parameter extractor. Analyze search prompts and return a JSON object with: 'maxPrice' (default 150), 'minReviews' (default 10), 'vibe' (default 'cozy'), and 'neighborhood' (default null or string if specified)."),
                Contents = new List<LlmContent>
                {
                    LlmContent.User($@"Analyze this user query: ""{prompt}""
Return a JSON object with:
- ""maxPrice"" (number, default 150)
- ""minReviews"" (number, default 10)
- ""vibe"" (string, default 'cozy')
- ""neighborhood"" (string or null, e.g. 'Islington')

Return ONLY the raw JSON object, no markdown, no other text.")
                }
            };
            
            State.AddStepLog("Acquisition Manager", "Parameter Extraction", "Extracting parameters from prompt using LLM...");
            State.SetActiveAgent("Acquisition Manager");
            var extractionResponse = await _llmService.GenerateContentAsync(extractionRequest);
            var jsonText = extractionResponse.Content?.Trim() ?? "{}";
            
            // Clean up markdown code blocks if the LLM wrapped it in ```json
            if (jsonText.StartsWith("```"))
            {
                var lines = jsonText.Split('\n');
                jsonText = string.Join("\n", lines.Where(l => !l.Trim().StartsWith("```")));
            }
            
            double maxPrice = 150;
            int minReviews = 10;
            string vibe = "cozy";
            string? neighborhood = null;

            try
            {
                using var parsedDoc = JsonDocument.Parse(jsonText);
                var root = parsedDoc.RootElement;
                if (root.TryGetProperty("maxPrice", out var pVal) && pVal.ValueKind == JsonValueKind.Number) maxPrice = pVal.GetDouble();
                if (root.TryGetProperty("minReviews", out var rVal) && rVal.ValueKind == JsonValueKind.Number) minReviews = rVal.GetInt32();
                if (root.TryGetProperty("vibe", out var vVal) && vVal.ValueKind == JsonValueKind.String) vibe = vVal.GetString() ?? "cozy";
                if (root.TryGetProperty("neighborhood", out var nVal) && nVal.ValueKind == JsonValueKind.String) neighborhood = nVal.GetString();
            }
            catch (Exception ex)
            {
                State.AddStepLog("Acquisition Manager", "Warning", $"Failed to parse parameters JSON: {ex.Message}. Falling back to default parameters.");
            }

            State.AddStepLog("Acquisition Manager", "Parameters Parsed", $"Extracted criteria: Max Price: ${maxPrice}, Min Reviews: {minReviews}, Vibe: '{vibe}', Neighborhood: '{neighborhood ?? "All"}'");

            // 2. Phase 1 — Polaris filtration (must complete before Vector can search)
            State.SetExecutionPhase("Filtering");
            State.SetActiveAgents(new[] { "Polaris Analyst" });
            var polarisPrompt = $"Task Assignment: Execute your specialty task with parameters: {{\"maxPrice\": {maxPrice}, \"minReviews\": {minReviews}, \"neighborhood\": \"{neighborhood ?? ""}\"}}. Run your tool and return the exact text results.";
            await PolarisAnalyst.RunAsync(polarisPrompt, _llmService);

            // 3. Phase 2 — Vector Specialist + Risk Auditor run in parallel now that Polaris has populated FilteredProperties
            State.SetExecutionPhase("Parallel Search");
            // Derive the neighborhood to audit from the Polaris results (or fallback to user-provided)
            string targetNeighborhood = neighborhood 
                ?? State.FilteredProperties.FirstOrDefault()?.Neighbourhood 
                ?? "Westminster";

            var vectorPrompt = $"Task Assignment: Execute your specialty task with parameters: {{\"vibe\": \"{vibe}\", \"neighborhood\": \"{targetNeighborhood}\"}}. Run your tool and return the exact text results.";
            var riskPrompt   = $"Task Assignment: Execute your specialty task with parameters: {{\"neighborhood\": \"{targetNeighborhood}\"}}. Run your tool and return the exact text results.";

            State.SetActiveAgents(new[] { "Vector Specialist", "Risk Auditor" });
            var vectorTask = Task.Run(() => VectorSpecialist.RunAsync(vectorPrompt, _llmService));
            var riskTask   = Task.Run(() => RiskAuditor.RunAsync(riskPrompt, _llmService));
            await Task.WhenAll(vectorTask, riskTask);

            // 3. Collate Results
            State.SetExecutionPhase("Collating Results");
            State.AddStepLog("Acquisition Manager", "Collation", "Collating data filters and semantic vibe matches...");

            var filteredIds = new HashSet<string>(State.FilteredProperties.Select(p => p.Id));
            var collatedMatches = State.FinalMatches
                .Where(p => filteredIds.Contains(p.Id))
                .ToList();

            if (collatedMatches.Count == 0)
            {
                State.AddStepLog("Acquisition Manager", "Collation Warning", "Strict intersection of filters and vibe matches was empty. Merging lists.");
                collatedMatches = State.FinalMatches; // Fallback
            }
            else
            {
                State.AddStepLog("Acquisition Manager", "Collation Success", $"Successfully intersected filters and vibe matches: {collatedMatches.Count} matches satisfy all criteria.");
            }
            State.FinalMatches = collatedMatches;
            State.SetActiveAgent("Acquisition Manager");
            State.AddStepLog("Acquisition Manager", "Collation Complete", $"Results collated. Dispatching to Financial Underwriter.");

            // 4. Financial Underwriting
            State.SetExecutionPhase("Underwriting Projections");
            var underwritePrompt = $"Task Assignment: Execute the UnderwriteProperties tool to underwrite the top collated properties. Run your tool and return the exact text results.";
            await FinancialUnderwriter.RunAsync(underwritePrompt, _llmService);

            var underwritingSummary = State.UnderwrittenProperties.Count > 0
                ? string.Join("\n", State.UnderwrittenProperties.Select(uw =>
                    $"- {uw.PropertyName}: Est. Value £{uw.EstimatedValue:N0} | Occupancy {(uw.OccupancyRate * 100):F0}% | Gross Revenue £{uw.GrossAnnualRevenue:N0}/yr | Net Income £{uw.NetAnnualIncome:N0}/yr | Cap Rate {(uw.RentalYield * 100):F1}% | Cash-on-Cash {(uw.CashOnCashReturn * 100):F1}%"))
                : "No underwriting data available.";

            // 5. Acquisition Manager Final Synthesis and Report Design
            State.SetExecutionPhase("Designing Prospectus");
            
            var coordinatorPrompt = $@"Your specialist agents have completed their tasks and provided the following data:

1. Data Filtration (Polaris Analyst): Found {State.FilteredProperties.Count} properties under ${maxPrice} with at least {minReviews} reviews.
2. Vibe Match (Vector Specialist): Top matched properties for vibe '{vibe}':
{string.Join("\n", State.FinalMatches.Take(3).Select(m => $"- {m.Name} in {m.Neighbourhood} (Price: ${m.Price}, Vibe Score: {m.VibeMatchScore:F2})"))}
3. Location Audit (Risk Auditor): Audited '{targetNeighborhood}'. Safety: {State.ActiveNeighborhoodProfile?.SafetyScore}, Crime: {State.ActiveNeighborhoodProfile?.CrimeRate}, Vibe: {State.ActiveNeighborhoodProfile?.Vibe}. Transit: {string.Join(" -> ", State.ActiveTransitRoute?.Path ?? new List<string>())} ({State.ActiveTransitRoute?.Hops} hops to Central London).
4. Financial Underwriting (Financial Underwriter):
{underwritingSummary}

Your task as Acquisition Manager:
1. Write a compelling, professional investment rationale that synthesises the property details, neighbourhood quality, transit connectivity, and the financial metrics (cap rate, cash-on-cash).
2. Call DelegateToProspectusDesigner with that rationale to generate the PDF.
3. Return the prospectus download link and a high-level summary to the user.";

            var result = await AcquisitionManager.RunAsync(coordinatorPrompt, _llmService);
            State.SetExecutionPhase("Finished");
            
            // Persist run details to disk
            var logPath = State.PersistRunHistory();
            State.AddStepLog("System", "Finished", $"Run persisted successfully to {logPath}");
            
            return result;
        }
        catch (Exception ex)
        {
            State.SetExecutionPhase("Error");
            State.AddChatMessage("Acquisition Manager", $"An orchestration error occurred: {ex.Message}", "Error");
            State.AddStepLog("System", "Error", $"Execution halted: {ex.Message}");
            return $"Orchestration failed: {ex.Message}";
        }
    }
}
