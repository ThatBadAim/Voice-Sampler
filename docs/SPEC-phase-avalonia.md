# SPEC: Avalonia Desktop App (Windows + Linux)

Replaces the WinUI 3 shell with Avalonia UI. Engine, CLI and `VoiceScan.App.Core` are unchanged except where noted.

## Acceptance criteria
1. `dotnet build VoiceScan.sln` and `dotnet test VoiceScan.sln` pass on Linux and Windows with no OS-conditional project settings.
2. No `Microsoft.UI`, `Windows.UI` or WinUI references remain in `app/` or `engine/`.
3. All four screens (Enrollment, Scan, Results, Review) load and bind to their existing view models.
4. Results and Review play real audio through `ffplay`; when `ffplay` is missing the play buttons are disabled with a tooltip.
5. Inference provider is visible: a banner shows when CUDA is not active. `-p:VoiceScanGpu=true` swaps in `Microsoft.ML.OnnxRuntime.Gpu`.
6. Review database and caches live under `LocalApplicationData/VoiceScan`, independent of the working directory.
7. No network calls at runtime. No decision thresholds or models changed, so no re-evaluation is required.
8. Self-contained publish for `linux-x64` (tarball + AppImage) and `win-x64` (zip).
