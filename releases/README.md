# Ollama Toolkit — Windows releases

Pre-built **OllamaToolkit.App** (WPF GUI) for Windows 11 x64.

## Latest: v1.6.0

| Download | Size | Requirements |
|----------|------|--------------|
| [OllamaToolkit.App-v1.6.0-win-x64.zip](OllamaToolkit.App-v1.6.0-win-x64.zip) | ~2 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| [v1.6.0 folder](v1.6.0/OllamaToolkit.App.exe) (unzipped) | ~3 MB | Same — run `OllamaToolkit.App.exe` directly |

**GitHub Release (recommended for most users):** [Releases page](https://github.com/rlewis3278/Ollama-AMD-Vulkan/releases/latest) includes a **self-contained** build (~150 MB) that does **not** require installing .NET.

### What's new in v1.6.0

- **Coding Tools** tab — all 16 `ollama launch` integrations (Claude Code, Codex, OpenCode, Copilot CLI, VS Code, etc.) with per-tool CLI option dropdowns and **Install All**
- Applies optimized benchmark settings (best compute mode, `num_ctx`, parallel) when launching with a local LLM
- **Responsive layout** — GUI scales from 1920×1080 to 2560×1600 without clipping
- **Global download indicator** — MB/s and ETA in the footer during pulls
- **Model Library substring search** and catalog filter improvements
- Testing highlight and stability fixes from v1.5.x

### Quick start

1. Download and extract the zip (or use the self-contained exe from GitHub Releases).
2. Install [Ollama for Windows](https://ollama.com/download) if not already installed.
3. Run `OllamaToolkit.App.exe`.
4. On first launch, use **Compute Modes** to pick CPU / APU / GPU / Hybrid and restart Ollama.

### Build from source

```powershell
.\scripts\Publish-ReleaseToGit.ps1
```

This refreshes `releases/v1.6.0/` and the zip files in this folder.