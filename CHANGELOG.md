# Changelog

All notable changes to `Glacier.Showcase` are documented in this file.

## [1.2.0]

- Standardized documentation (`README.md`, `docs/manual.md`, `docs/architecture.md`) and aligned filename casing (`docs/architecture.md`).
- Integrated all 15 Glacier ecosystem packages across `/` (`AgentService`, `ShowcaseIntentRouter`), `/mission-control` (`MissionControlService` with `PagedBlockPool` and `GraphStore`), and `/gpu` (`GpuAccelerationBenchmarkService`).
- Verified 46 unit and integration tests in Release (`Category!=Hardware` filter supported for headless Linux CI runners).
