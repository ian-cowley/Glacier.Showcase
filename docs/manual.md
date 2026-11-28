# Glacier.Showcase User Manual (`v1.2.0`)

`Glacier.Showcase` is an ASP.NET Core Blazor InteractiveServer web application demonstrating 15 Glacier ecosystem libraries in a single desktop-friendly UI.

## 1. Running the Application

### Standard Local Run

```bash
dotnet run --project Glacier.Showcase.csproj -c Release
```

Open the localhost URL shown in the terminal output. Use the navigation bar to switch between the three interactive views: `/` (Advisor Cockpit), `/mission-control` (System Telemetry), and `/gpu` (Live GPU Benchmarks).

### Environment & Configuration

Copy `appsettings.template.json` to an untracked `appsettings.Development.json` (gitignored) or set environment variables directly in your shell:

```powershell
$env:GEMINI_API_KEY = "<your-key>"           # Optional: cloud Gemini reasoning & embeddings
$env:GLACIER_MODELS_DIR = "D:\Models"        # Optional: local GGUF / GLTB directory scan
$env:GLACIER_LISTINGS_CSV = "D:\listings.csv"# Optional: custom Airbnb CSV dataset
$env:GLACIER_NPU_ENDPOINT = "http://127.0.0.1:1234/v1"
$env:GLACIER_NPU_EMBED_MODEL = "nomic-embed"
```

When `GEMINI_API_KEY` is omitted, intent classification, tabular filtering, vector similarity matrices, Mission Control telemetry, and GPU benchmarks still execute locally.

## 2. Page Reference

### `/` — Multi-Agent Investment Advisor (`Home.razor`)

Combines natural-language routing (`ShowcaseIntentRouter` powered by `Glacier.Clavier` + source-generated regex extraction) with `AgentService` and `AgentOrchestratorState` telemetry:

- **Full Prospectus**: Runs neighborhood scoring (`NeighborhoodAssessmentService`), `Glacier.Rag` retrieval, `Glacier.Plot` chart generation, and `Glacier.DocTree` / `PdfReportService` PDF output.
- **Budget Filter**: Filters `Glacier.Polaris` tabular listings (`AirbnbDataService`) by extracted nightly rate, rooms, or borough aliases (for example `"Notting Hill"` -> `"Kensington and Chelsea"`, `"Shoreditch"` -> `"Hackney"`).
- **Vibe Search**: Performs semantic similarity ranking using `Glacier.Vector` and `GeminiEmbeddingClient` (when configured).
- **Neighborhood Audit**: Aggregates district metrics and statistical comparisons across London boroughs.

The right-hand telemetry panel displays live `TelemetryAgent` state (`Thinking` / `Idle`), model inputs, and execution logs streamed via `AgentOrchestratorState`.

### `/mission-control` — Hardware, KV Pool & Graph Cockpit (`MissionControl.razor`)

Powered by `MissionControlService`:

- Discovers local compute adapters (`Glacier.Gpu`), local NPU endpoints (`GLACIER_NPU_ENDPOINT`), and model files under `GLACIER_MODELS_DIR`.
- Displays a live `PagedBlockPool` KV-cache simulator (`totalBlocks: 64`, `layers: 28`, `headsKv: 4`, `headDim: 128`, `blockSize: 16`) from `Glacier.Inference`.
- Runs live `GraphStore` / `GraphSearch` traversal and an 8x8 `Glacier.Vector` cosine-similarity matrix.
- Optionally polls a running `Glacier.Serve` `/health` endpoint (`ServeHealthUrl`) for inference server metrics.

### `/gpu` — Bare-Metal GPU & SIMD Acceleration (`GpuShowcase.razor`)

Powered by `GpuAccelerationBenchmarkService`, this view runs side-by-side live CPU vs GPU workloads on your own machine across:

- `Glacier.Polaris` columnar aggregation
- `Glacier.ML` training/inference kernels
- `Glacier.Tensor` matrix ops
- `Glacier.Vector` similarity search
- `Glacier.Plot` / `Glacier.Plot.Statistical` live chart rendering of each run

## 3. Running Tests

```bash
dotnet test tests/Glacier.Showcase.Tests/Glacier.Showcase.Tests.csproj -c Release
```

CI runs `Glacier.Showcase.Tests` on `windows-latest` and filters `[Trait("Category", "Hardware")]` tests via `--filter "Category!=Hardware"` on headless `ubuntu-latest` runners (`.github/workflows/build.yml`).
