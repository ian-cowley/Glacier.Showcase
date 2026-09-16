using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Glacier.Showcase.Services;

public class ChatMessage
{
    public string Sender { get; set; } = "Client"; // e.g. "Client", "Acquisition Manager", "Polaris Analyst", etc.
    public string Content { get; set; } = "";
    public string Vibe { get; set; } = "Normal"; // e.g. "Thinking", "Success", "Error"
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public class PropertyUnderwriting
{
    public string PropertyId { get; set; } = "";
    public string PropertyName { get; set; } = "";
    public double EstimatedMonthlyRent { get; set; }
    public double OccupancyRate { get; set; }
    public double GrossAnnualRevenue { get; set; }
    public double OperatingExpenses { get; set; }
    public double NetAnnualIncome { get; set; }
    public double EstimatedValue { get; set; }
    public double RentalYield { get; set; }
    public double CashOnCashReturn { get; set; }
}

public class AgentLogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string AgentName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty; // e.g., "Model Input", "Tool Call", "Tool Return", "Error", "Finished"
    public string Description { get; set; } = string.Empty;
    public string RawContent { get; set; } = string.Empty;
}

public class AgentStateInfo
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<string> Tools { get; set; } = new();
    public string Status { get; set; } = "Idle"; // "Idle", "Thinking", "Executing Tool", "Done", "Error"
}

public class AgentOrchestratorState
{
    public List<ChatMessage> ChatMessages { get; } = new();
    public List<AgentLogEntry> StepLogs { get; } = new();
    public Dictionary<string, List<AgentLogEntry>> AgentHistories { get; } = new();
    public Dictionary<string, AgentStateInfo> RegisteredAgents { get; } = new();
    
    public string ActiveAgentName { get; private set; } = "";
    public HashSet<string> ActiveAgentNames { get; private set; } = new();
    public string ExecutionPhase { get; private set; } = "Ready";
    public string? SelectedAgentForInspector { get; private set; }

    // Visualizer Outputs
    public List<PropertyMatch> FilteredProperties { get; set; } = new();
    public List<PropertyMatch> FinalMatches { get; set; } = new();
    public List<PropertyUnderwriting> UnderwrittenProperties { get; set; } = new();
    public TransitRoute? ActiveTransitRoute { get; set; }
    public NeighborhoodProfile? ActiveNeighborhoodProfile { get; set; }
    public string? ProspectusPdfPath { get; set; }

    public event Action? OnChange;

    public AgentOrchestratorState()
    {
        // Pre-register our expected agents so the SVG nodes have static description cards immediately
        RegisterAgent(new AgentStateInfo
        {
            Name = "Acquisition Manager",
            Role = "Team Coordinator",
            Instructions = "Directs the investment inquiry, manages workflow tasks, coordinates specialists, and synthesizes the final investment overview for the user.",
            Tools = new List<string> { "DelegateToPolaris", "DelegateToVector", "DelegateToRiskAuditor", "DelegateToDesigner" }
        });

        RegisterAgent(new AgentStateInfo
        {
            Name = "Polaris Analyst",
            Role = "Data Filtration Specialist",
            Instructions = "Scans massive housing databases using Glacier.Polaris. Filters raw properties by price limits and reviews directly in-engine.",
            Tools = new List<string> { "FilterProperties" }
        });

        RegisterAgent(new AgentStateInfo
        {
            Name = "Vector Specialist",
            Role = "Semantic Search Specialist",
            Instructions = "Generates high-dimensional vector embeddings of listing descriptions, indexes them, and executes SIMD cosine similarity searches to identify vibe matches.",
            Tools = new List<string> { "SemanticSearch" }
        });

        RegisterAgent(new AgentStateInfo
        {
            Name = "Risk Auditor",
            Role = "Neighborhood & Transit Auditor",
            Instructions = "Inspects local safety scores using lazy-deserialized DocTrees and determines transit proximity to central London via shortest-path BFS over binary CSR Graph databases.",
            Tools = new List<string> { "AuditNeighborhood" }
        });

        RegisterAgent(new AgentStateInfo
        {
            Name = "Prospectus Designer",
            Role = "Report Publisher",
            Instructions = "Gathers property particulars and transit risk assessments, formats high-fidelity investment summaries, and generates print-ready PDFs using TinyPdf.",
            Tools = new List<string> { "GenerateReport" }
        });

        RegisterAgent(new AgentStateInfo
        {
            Name = "Financial Underwriter",
            Role = "Financial Analyst",
            Instructions = "Formulates comprehensive financial underwriting projections, calculating gross annual revenue, operating expenses, estimated valuation, cap rate / rental yield, and cash-on-cash return based on nightly price and neighborhood metrics.",
            Tools = new List<string> { "UnderwriteProperties" }
        });
    }

    public void RegisterAgent(AgentStateInfo info)
    {
        RegisteredAgents[info.Name] = info;
        if (!AgentHistories.ContainsKey(info.Name))
        {
            AgentHistories[info.Name] = new List<AgentLogEntry>();
        }
        NotifyStateChanged();
    }

    public void SetActiveAgent(string agentName)
    {
        ActiveAgentName = agentName;
        ActiveAgentNames = new HashSet<string> { agentName };
        foreach (var key in RegisteredAgents.Keys)
        {
            if (RegisteredAgents[key].Status != "Idle" && RegisteredAgents[key].Status != "Error")
            {
                RegisteredAgents[key].Status = "Idle";
            }
        }
        if (RegisteredAgents.TryGetValue(agentName, out var info))
        {
            info.Status = "Thinking";
        }
        NotifyStateChanged();
    }

    public void SetActiveAgents(IEnumerable<string> agentNames)
    {
        var names = agentNames.ToList();
        ActiveAgentNames = new HashSet<string>(names);
        ActiveAgentName = names.FirstOrDefault() ?? "";
        foreach (var key in RegisteredAgents.Keys)
        {
            RegisteredAgents[key].Status = names.Contains(key) ? "Thinking" : "Idle";
        }
        NotifyStateChanged();
    }

    public void SetAgentStatus(string agentName, string status)
    {
        if (RegisteredAgents.TryGetValue(agentName, out var info))
        {
            info.Status = status;
        }
        NotifyStateChanged();
    }

    public void SetExecutionPhase(string phase)
    {
        ExecutionPhase = phase;
        NotifyStateChanged();
    }

    public void SelectAgentForInspection(string? agentName)
    {
        SelectedAgentForInspector = agentName;
        NotifyStateChanged();
    }

    public void AddChatMessage(string sender, string content, string vibe = "Normal")
    {
        ChatMessages.Add(new ChatMessage { Sender = sender, Content = content, Vibe = vibe });
        NotifyStateChanged();
    }

    public void AddStepLog(string agentName, string action, string description, string rawContent = "")
    {
        var entry = new AgentLogEntry
        {
            AgentName = agentName,
            Action = action,
            Description = description,
            RawContent = rawContent
        };

        StepLogs.Add(entry);

        if (!AgentHistories.TryGetValue(agentName, out var history))
        {
            history = new List<AgentLogEntry>();
            AgentHistories[agentName] = history;
        }
        history.Add(entry);

        NotifyStateChanged();
    }

    public void Reset()
    {
        ChatMessages.Clear();
        StepLogs.Clear();
        FilteredProperties.Clear();
        FinalMatches.Clear();
        UnderwrittenProperties.Clear();
        ActiveTransitRoute = null;
        ActiveNeighborhoodProfile = null;
        ProspectusPdfPath = null;
        SelectedAgentForInspector = null;
        ActiveAgentName = "";
        ActiveAgentNames = new HashSet<string>();
        ExecutionPhase = "Ready";

        foreach (var agent in RegisteredAgents.Values)
        {
            agent.Status = "Idle";
            AgentHistories[agent.Name].Clear();
        }

        NotifyStateChanged();
    }

    /// <summary>
    /// Persists the complete orchestration run history to disk as a JSON report.
    /// </summary>
    public string PersistRunHistory()
    {
        try
        {
            string folder = Path.Combine(AppContext.BaseDirectory, "wwwroot", "reports", "runs");
            Directory.CreateDirectory(folder);

            string filename = $"run_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string filepath = Path.Combine(folder, filename);

            var runReport = new
            {
                Timestamp = DateTime.Now,
                ExecutionPhase = ExecutionPhase,
                FinalMatchesCount = FinalMatches.Count,
                ProspectusPdfPath = ProspectusPdfPath,
                PdfPath = ProspectusPdfPath, // legacy support
                FilteredProperties = FilteredProperties,
                FinalMatches = FinalMatches,
                UnderwrittenProperties = UnderwrittenProperties,
                ActiveNeighborhoodProfile = ActiveNeighborhoodProfile,
                ActiveTransitRoute = ActiveTransitRoute,
                ChatHistory = ChatMessages,
                ExecutionLogs = StepLogs,
                AgentTelemetry = AgentHistories
            };

            string json = JsonSerializer.Serialize(runReport, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filepath, json);

            return $"/reports/runs/{filename}";
        }
        catch (Exception ex)
        {
            AddStepLog("System", "Error", $"Failed to persist run history: {ex.Message}");
            return string.Empty;
        }
    }

    private void NotifyStateChanged() => OnChange?.Invoke();
}
