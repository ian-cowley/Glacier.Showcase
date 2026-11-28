# Glacier.Showcase Architecture (`v1.2.0`)

`Glacier.Showcase` is a Blazor Server (`.NET 10`, `InteractiveServer`) application that exercises 15 Glacier packages inside a single process without sidecar Python runtimes.

## Solution Layout

```text
Glacier.Showcase
├── Components/Pages/
│   ├── Home.razor             Multi-agent chat, intent trace & PDF report view (/)
│   ├── MissionControl.razor   Hardware, KV pool, GraphStore & Serve telemetry (/mission-control)
│   ├── GpuShowcase.razor      Interactive CPU vs GPU microbenchmark runner (/gpu)
│   ├── Error.razor            Exception fallback view
│   └── NotFound.razor         404 route fallback
├── Services/
│   ├── AgentService.cs                    LlmAgent orchestrator + TelemetryAgent instrumentation
│   ├── ShowcaseIntentRouter.cs            System-1 Clavier + regex intent & parameter extraction
│   ├── AgentOrchestratorState.cs          Live UI pub/sub log & agent status state
│   ├── AirbnbDataService.cs               Glacier.Polaris tabular dataset loader & query layer
│   ├── NeighborhoodAssessmentService.cs   Statistical district scoring
│   ├── PdfReportService.cs                PDF prospectus renderer
│   ├── GeminiEmbeddingClient.cs           Optional cloud embedding & Gemini client
│   ├── MissionControlService.cs           GPU/NPU discovery, PagedBlockPool, GraphStore, /health poll
│   └── GpuAccelerationBenchmarkService.cs Interactive hardware vs CPU benchmark harness
└── tests/Glacier.Showcase.Tests/          46 unit & integration tests
```

## 1. Local Intent Routing + Agent Orchestration

User queries arriving at `/` pass through `ShowcaseIntentRouter` (`Glacier.Clavier.Engine.ClavierSession` + compiled source-generated regexes) before invoking multi-turn cloud LLM tools:

1. **Classification**: Maps input into `ShowcaseSearchIntent` (`FullProspectus`, `BudgetFilter`, `VibeSearch`, `NeighborhoodAudit`) with confidence score and millisecond latency.
2. **Canonical Extraction**: Normalizes London borough aliases (e.g., `"Brick Lane"` -> `"Tower Hamlets"`, `"Battersea"` -> `"Wandsworth"`) and extracts numeric budget/capacity constraints into `ExtractedSearchParameters`.
3. **Instrumented Execution**: `AgentService` runs `TelemetryAgent` steps wired through `ToolCancellationScope` (`AsyncLocal<CancellationToken>`) and emits step traces to `AgentOrchestratorState`, updating the Razor UI asynchronously.

## 2. Mission Control Telemetry (`MissionControlService`)

`MissionControlService` runs periodic background probes to aggregate live process and ecosystem state:

- **Compute & Model Inventory**: Probes available GPUs, checks `GLACIER_NPU_ENDPOINT` readiness, and inventories `.gguf`/`.gltb` artifacts.
- **KV Cache Allocation**: Instantiates a 64-block `Glacier.Inference` `PagedBlockPool` (`28` layers, `4` KV heads, `headDim = 128`, `blockSize = 16`) to demonstrate paged attention memory accounting.
- **Graph & Vector Diagnostics**: Executes `GraphStore`/`GraphSearch` path queries (`Glacier.Graph`) and computes an 8x8 vector similarity heatmap (`Glacier.Vector`).

## 3. Referenced Glacier Stack (15 Packages)

| Layer | Packages |
| :--- | :--- |
| **Compute & Linear Algebra** | `Glacier.Gpu`, `Glacier.Tensor`, `Glacier.Polaris`, `Glacier.ML` |
| **LLM Runtime & Serving** | `Glacier.Inference`, `Glacier.Serve`, `Glacier.Tune`, `Glacier.Clavier` |
| **Retrieval, Memory & Agents** | `Glacier.Vector`, `Glacier.Graph`, `Glacier.Rag`, `Glacier.DocTree`, `Glacier.Agent` |
| **Rendering & Visualization** | `Glacier.Graphics`, `Glacier.Plot` |
