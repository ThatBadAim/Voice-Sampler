# AGENTS.md — Agent Operating Guidelines for VoiceScan

## 1. Project Summary & Stack
VoiceScan is a fully offline cross-platform (Windows + Linux) application that enrolls a person's voice and scans hours of audio (especially gameplay recordings with voice chat mixed with game audio) to pinpoint where they speak.
- **Engine & CLI:** C# / .NET 8+ (`/engine`), ONNX Runtime with CUDA EP for GPU inference, SQLite for profiles and embedding cache, FFmpeg for chunked audio decode.
- **Desktop UI:** Avalonia UI 12 (`/app/VoiceScan.Avalonia`), one codebase for Windows and Linux; view models and services live in `/app/VoiceScan.App.Core`.
- **Research & Eval:** Python (`/research` experiments, `/eval` synthetic data and harness). Python outputs ONNX models and configs; the C# engine loads models directly.
- **Strict Offline Privacy:** 100% offline runtime. No network calls, no telemetry.

## 2. Build, Test & Knowledge Graph Commands
- **Build Solution:** `dotnet build VoiceScan.sln`
- **Run Engine Tests:** `dotnet test VoiceScan.sln`
- **Run Engine CLI:** `dotnet run --project engine/VoiceScan.Cli -- [args]`
- **Export Evidence Report:** `dotnet run --project engine/VoiceScan.Cli -- report export --results <scan.json> --output <dir> --profile <name>`
- **Query Architecture:** `graphify query "<question>"` | `graphify path "<NodeA>" "<NodeB>"`
- **Update Graph:** `graphify update .` (must run before completing any task)

## 3. Shared Workflow Rules
- **Specs First:** Read `AGENTS.md`, `DESIGN.md`, `docs/BASE-VERSION.md`, and the active `docs/SPEC-phase*.md` before writing code.
- **Graphify First:** Query graphify before grep or whole-repo scans. Update graph before declaring tasks complete.
- **Evaluation Integrity:** Never read, touch, or tune thresholds against held-out test data (stored outside repo). Never change decision boundaries without re-running the evaluation harness (`/eval`).
- **Atomic Progress:** Small commits (one per task), test all new logic, report changes, test output, and blockers.
- **Scope Discipline:** Never implement features beyond the current task. If an external defect is found, note it and stop.

## 4. Anti-AI Slop Directives
- **No Stubs:** Never commit `TODO`, `pass`, `NotImplementedException`, or mock implementations unless explicitly requested in a spec. Everything committed must be functional and wired up.
- **KISS & YAGNI:** Do not introduce premature abstractions, factory hierarchies, or redundant wrappers.
- **Zero Fluff:** Omit conversational filler. Report exact changes, commands run, and verification outcomes.
- **No Hallucinated Comments:** Code comments are strictly for non-obvious invariants, math, and thread-safety.
- **Ground Truth Over Speculation:** Verify library and API signatures with CLI probes or references. Report true benchmark numbers.

## 5. Coding Conventions
- **C#:** File-scoped namespaces, nullable reference types enabled, PascalCase for types/methods, camelCase for parameters/locals. Stream audio chunks; never load multi-hour files into RAM.
- **Python:** PEP 8, strict type hints, deterministic random seeds, no runtime dependencies leaked into production.
- **Error Handling:** Explicit result types or domain exceptions; always surface GPU fallback warnings.

## 6. Where Specs Live
- **Architecture & System Design:** `DESIGN.md`
- **Base Version Acceptance & Checklist:** `docs/BASE-VERSION.md`
- **Phase Acceptance Specifications:** `docs/SPEC-phase*.md` (Current: `docs/SPEC-phase1.md`; active feature work: `docs/SPEC-phase-moderation.md`, `docs/SPEC-model-swap.md`, `docs/SPEC-scan-speed-accuracy.md`)
- **Model & Dataset Licenses:** `docs/LICENSES.md`
- **Evaluation Reports & Logs:** `docs/` (`baseline-report.md`, `accuracy-log.md`)

