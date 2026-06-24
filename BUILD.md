# C# Build (Greenfield)

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 11 + AMD Vulkan drivers (`vulkaninfo.exe`)
- Ollama for Windows (optional for API features)

## Build

```powershell
cd C:\Users\lewis\Ollama-AMD-Vulkan
dotnet build OllamaToolkit.sln -c Release
```

## Run GUI (dev)

```powershell
dotnet run --project src/OllamaToolkit.App/OllamaToolkit.App.csproj -c Release
```

Or double-click `Ollama-Mode-GUI.cmd`.

## CLI

```powershell
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- status
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- set-mode GPU
dotnet run --project src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -- vulkan-devices
```

## Publish

```powershell
dotnet publish src/OllamaToolkit.App/OllamaToolkit.App.csproj -c Release -r win-x64 -o publish/OllamaToolkit.App
dotnet publish src/OllamaToolkit.Cli/OllamaToolkit.Cli.csproj -c Release -r win-x64 -o publish/OllamaToolkit.Cli
```

## Solution layout

| Project | Role |
|---------|------|
| `OllamaToolkit.Core` | Modes, env vars, Ollama API, Vulkan discovery |
| `OllamaToolkit.Cli` | `set-mode`, `vulkan-devices`, `status` |
| `OllamaToolkit.BenchmarkStore` | Model profiles, report import |
| `OllamaToolkit.BenchmarkRunner` | 4-mode automated benchmark |
| `OllamaToolkit.AiAssist` | Plain-language errors, AI feature keys |
| `OllamaToolkit.App` | WPF GUI — Compute Modes, Models & Launch, Testing, Model Run chat, AI Features toggles |
| Other libraries | Stubs for catalog/registry/category (next phase) |

## Functional GUI tabs (current build)

- **Compute Modes** — mode cards, env display, apply + restart
- **Models & Launch** — profile grid, launch in best mode, import reports
- **Testing Suite** — benchmark queue (selected / untested)
- **Model Run** — streaming chat + Open in PowerShell
- **AI Activity** — activity log tail
- **AI Features** — per-function on/off toggles

PowerShell scripts remain on disk until later phases remove them. User data in `%USERPROFILE%\.ollama-amd-vulkan\` is unchanged.