# Ollama Toolkit — Windows releases

Pre-built **OllamaToolkit.App** (WPF GUI) for Windows 11 x64.

## Latest: v1.1.0

| Download | Size | Requirements |
|----------|------|--------------|
| [OllamaToolkit.App-v1.1.0-win-x64.zip](OllamaToolkit.App-v1.1.0-win-x64.zip) | ~1 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |
| [v1.1.0 folder](v1.1.0/OllamaToolkit.App.exe) (unzipped) | ~2 MB | Same — run `OllamaToolkit.App.exe` directly |

**GitHub Release (recommended for most users):** [Releases page](https://github.com/rlewis3278/Ollama-AMD-Vulkan/releases/latest) includes a **self-contained** build (~150 MB) that does **not** require installing .NET.

### Quick start

1. Download and extract the zip (or use the self-contained exe from GitHub Releases).
2. Install [Ollama for Windows](https://ollama.com/download) if not already installed.
3. Run `OllamaToolkit.App.exe`.
4. On first launch, use **Compute Modes** to pick CPU / APU / GPU / Hybrid and restart Ollama.

### Build from source

```powershell
.\scripts\Publish-ReleaseToGit.ps1
```

This refreshes `releases/v1.1.0/` and the zip files in this folder.