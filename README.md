# Ollama AMD Vulkan Manager

Professional PowerShell toolkit for switching Ollama compute modes on **AMD Ryzen 9 6900HX + Radeon 680M iGPU + RX 6700S** laptops running **Windows 11**, using the **Vulkan** backend.

## What This Toolkit Does

| Script | Purpose |
|--------|---------|
| `Ollama-Mode-GUI.ps1` | **GUI** — click to set mode and restart Ollama |
| `Set-OllamaMode.ps1` | **Daily use** — set mode and restart Ollama in one command |
| `Ollama-AMD-Vulkan-Manager.ps1` | Switch between CPU / APU / dGPU / Hybrid modes |
| `Ollama-VulkanWorkaround.ps1` | **680M fix** — use system Vulkan loader instead of Ollama bundled DLL |
| `Get-VulkanDevices.ps1` | Identify Vulkan GPU indices (680M vs 6700S) |
| `Test-OllamaBenchmark.ps1` | Benchmark downloaded models via Ollama REST API |

## Quick Start

### 1. Prerequisites

- Windows 11 with current AMD Adrenalin drivers (Vulkan included)
- [Ollama for Windows](https://ollama.com/download) installed and running
- PowerShell 5.1+ or PowerShell 7+
- `vulkaninfo` available (bundled with AMD drivers at `%SystemRoot%\System32\vulkaninfo.exe`)

### 2. Unblock scripts (first run only)

```powershell
cd C:\Users\lewis\Ollama-AMD-Vulkan
Unblock-File .\*.ps1
```

### 3. Verify Vulkan devices

```powershell
.\Get-VulkanDevices.ps1
```

### Important: Two Different GPU Numbering Systems

| System | GPU 0 | GPU 1 |
|--------|-------|-------|
| **Windows Task Manager** | RX 6700S (dGPU) | Radeon 680M (iGPU) |
| **Vulkan / Ollama** (`vulkaninfo`) | Radeon 680M (iGPU) | RX 6700S (dGPU) |

`GGML_VK_VISIBLE_DEVICES` uses **Vulkan indices**, not Task Manager labels. The toolkit auto-detects the correct values:

| Mode | `GGML_VK_VISIBLE_DEVICES` | Targets |
|------|---------------------------|---------|
| APU | `0` (Vulkan GPU0) | Radeon 680M |
| GPU | `1` (Vulkan GPU1) | RX 6700S |
| Hybrid | `0,1` | Both GPUs |

### 4. Set mode for everyday Ollama / Llama use (recommended)

**GUI (easiest):** double-click `Ollama-Mode-GUI.cmd` or run:

```powershell
.\Ollama-Mode-GUI.ps1
```

Click a mode card (CPU / APU / GPU / Hybrid). Ollama restarts automatically when the checkbox is enabled.

**CLI:** one command applies the mode, restarts Ollama, and waits until the API is ready:

```powershell
.\Set-OllamaMode.ps1 GPU
```

Other modes:

```powershell
.\Set-OllamaMode.ps1 CPU
.\Set-OllamaMode.ps1 APU
.\Set-OllamaMode.ps1 Hybrid
```

Check current mode without changing anything:

```powershell
.\Set-OllamaMode.ps1 -ShowStatus
```

Apply variables only (no restart):

```powershell
.\Set-OllamaMode.ps1 GPU -NoRestart
```

After switching, run models as usual — e.g. `ollama run llama3.2:3b` or your desktop Ollama app.

**Recommended modes from benchmarks on this machine:**

| Use case | Mode |
|----------|------|
| Small/fast models (e.g. llama3.2:3b) | `GPU` |
| phi4-mini, qwen2.5-coder:7b | `CPU` |
| Large models that fail on GPU | `CPU` |

### 5. Switch compute mode (advanced)

**Interactive menu:**

```powershell
.\Ollama-AMD-Vulkan-Manager.ps1
```

**CLI (persistent, User scope) with auto-restart:**

```powershell
.\Ollama-AMD-Vulkan-Manager.ps1 -Mode GPU -Force -RestartOllama
```

**CLI without restart:**

```powershell
.\Ollama-AMD-Vulkan-Manager.ps1 -Mode GPU -Force
```

**Session-only (not persisted after closing PowerShell):**

```powershell
.\Ollama-AMD-Vulkan-Manager.ps1 -Mode Hybrid -Scope Process -Force
```

### 6. Restart Ollama manually

If you used `-NoRestart` or the manager without `-RestartOllama`, Ollama must be restarted for mode changes to apply:

1. Right-click the Ollama tray icon → **Quit Ollama**
2. Start Ollama from the Start Menu
3. Confirm settings:

```powershell
.\Set-OllamaMode.ps1 -ShowStatus
```

### 7. Run a benchmark

```powershell
.\Test-OllamaBenchmark.ps1
```

Or non-interactive:

```powershell
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode GPU -Force
```

Other benchmark commands:

```powershell
# List downloaded models only
.\Test-OllamaBenchmark.ps1 -ListModels

# Print four-mode comparison workflow
.\Test-OllamaBenchmark.ps1 -CompareModes -ModelName "qwen2.5-coder:7b"

# Average 3 timed runs
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode GPU -Runs 3 -Force

# Launch benchmark from manager
.\Ollama-AMD-Vulkan-Manager.ps1 -RunBenchmark
```

### Fully automated test (stop/restart Ollama, all modes, screenshots, HTML report)

```powershell
# Full suite: CPU + APU + GPU + Hybrid, auto-restart Ollama each mode
.\Invoke-OllamaAutomatedTest.ps1 -ModelName "qwen2.5-coder:7b"

# Quick automated test (subset of modes)
.\Invoke-OllamaAutomatedTest.ps1 -ModelName "llama3.2:3b" -Modes GPU,CPU -Runs 1 -NumPredict 32

# ALL downloaded models (full suite per model + master report)
.\Invoke-OllamaAutomatedTestAll.ps1

# Heavier all-models run (more accurate, much longer)
.\Invoke-OllamaAutomatedTestAll.ps1 -NumPredict 64 -Runs 2 -ApiTimeoutSec 600
```

Output folder `reports\<timestamp>\` contains:

| File | Description |
|------|-------------|
| `report.html` | Full visual comparison report |
| `report.json` | Machine-readable results |
| `session-results.csv` | Per-mode CSV metrics |
| `report-window.png` | Screenshot of HTML report |
| `report-fullscreen.png` | Full desktop screenshot |
| `screenshots\mode-*.png` | Per-mode result cards |

Results append to `ollama-benchmark-results.csv` in the script directory when using `Test-OllamaBenchmark.ps1` directly.

---

## Compute Modes

| Mode | `OLLAMA_VULKAN` | `HIP_VISIBLE_DEVICES` | `GGML_VK_VISIBLE_DEVICES` | `OLLAMA_IGPU_ENABLE` | Use Case |
|------|-----------------|----------------------|----------------------------|----------------------|----------|
| **CPU** | `0` | `-1` | `-1` | `0` | Debugging, max compatibility, no GPU |
| **APU** | `1` | `-1` | `0` (Vulkan) | **`1`** | iGPU only (680M) — **required** on Windows |
| **GPU** | `1` | `-1` | `1` (Vulkan) | `0` | Best single-GPU throughput (6700S) |
| **Hybrid** | `1` | `-1` | `0,1` (Vulkan) | `1` | Split layers across both GPUs |

`HIP_VISIBLE_DEVICES=-1` disables the HIP backend so Vulkan is the sole GPU path on Windows.

APU mode also sets `OLLAMA_NUM_GPU=999` and applies the system Vulkan loader workaround automatically.

### Manager CLI Reference

```powershell
# Show current mode and variables
.\Ollama-AMD-Vulkan-Manager.ps1 -ShowStatus

# List Vulkan devices
.\Ollama-AMD-Vulkan-Manager.ps1 -ListDevices

# Restore previous User-scope variables from backup
.\Ollama-AMD-Vulkan-Manager.ps1 -RestoreBackup
```

Backups are stored at `%USERPROFILE%\.ollama-amd-vulkan\env-backup.json` before each persistent change.

---

## Benchmarking Guide

### Recommended Workflow: Compare All Four Modes

Use the **same model**, **same prompt**, and **same power state** for fair comparisons.

```powershell
# 1. Pull a good baseline model (if not already downloaded)
ollama pull qwen2.5:7b
ollama pull llama3.1:8b

# 2. For each mode, switch → restart Ollama → benchmark
.\Ollama-AMD-Vulkan-Manager.ps1 -Mode CPU   -Force
# Restart Ollama, then:
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode CPU -Force

.\Ollama-AMD-Vulkan-Manager.ps1 -Mode APU   -Force
# Restart Ollama, then:
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode APU -Force

.\Ollama-AMD-Vulkan-Manager.ps1 -Mode GPU   -Force
# Restart Ollama, then:
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode GPU -Force

.\Ollama-AMD-Vulkan-Manager.ps1 -Mode Hybrid -Force
# Restart Ollama, then:
.\Test-OllamaBenchmark.ps1 -ModelName "qwen2.5-coder:7b" -Mode Hybrid -Force
```

### Recommended Models for This Hardware

Start with **7B–8B class models at Q4/Q5** — they fit comfortably in 8 GB VRAM and produce meaningful GPU comparisons:

| Model | Command | Notes |
|-------|---------|-------|
| Qwen 2.5 7B | `ollama pull qwen2.5:7b` | Strong general-purpose baseline |
| Qwen 2.5 Coder 7B | `ollama pull qwen2.5-coder:7b` | Good for coding workloads |
| Llama 3.1 8B | `ollama pull llama3.1:8b` | Solid English reasoning |
| Mistral 7B | `ollama pull mistral:7b` | Fast, efficient |
| Gemma 2 9B (Q4) | `ollama pull gemma2:9b` | Upper VRAM limit; watch 6700S usage |

Avoid using 14B+ models (e.g. Phi-4 14B, Qwen 3.6 36B) for **cross-mode GPU comparison** on this laptop — they may spill to CPU/RAM and skew results.

### What Gets Measured

The benchmark uses `POST /api/generate` with `stream: false` and records:

| Metric | Source | Meaning |
|--------|--------|---------|
| **PromptEval_tps** | `prompt_eval_count` / `prompt_eval_duration` | Prompt ingestion speed |
| **Generation_tps** | `eval_count` / `eval_duration` | Token generation speed (primary metric) |
| **TTFT_ms** | `load_duration` + `prompt_eval_duration` | Estimated time to first token |
| **VRAM_MB** | `GET /api/ps` → `size_vram` | Model VRAM after load |

A **warm-up run** (32 tokens) executes before the recorded benchmark to stabilize GPU clocks and model weights in VRAM.

### CSV Output Columns

```
Timestamp, Mode, Model, Quantization, PromptEval_tps, Generation_tps, TTFT_ms, VRAM_MB, Notes
```

Open in Excel or import into a spreadsheet to chart mode comparisons.

### Monitoring During Benchmarks

While a run is in progress:

1. **Task Manager** → Performance
   - **GPU 0** = RX 6700S (dGPU) — watch **Dedicated GPU memory** and **3D** utilization
   - **GPU 1** = Radeon 680M (iGPU) — shared system memory
   - Note: Ollama uses Vulkan indices (GPU0=680M, GPU1=6700S), which are reversed from Task Manager
2. **AMD Software** → Performance → Metrics
   - GPU utilization, VRAM, temperature, power draw
3. Keep the laptop **plugged in** with **Best Performance** power plan for consistent results

### Interpreting Results Across Modes

| Mode | Expected Behavior |
|------|-------------------|
| **CPU** | Slowest generation; no GPU activity; useful baseline |
| **APU** | Moderate speed; low power; 680M VRAM is shared with system RAM |
| **GPU** | Usually **fastest single-stream** throughput on 6700S 8 GB |
| **Hybrid** | May improve large-model capacity by splitting layers; throughput can be higher or lower depending on PCIe/memory bandwidth and scheduler behavior |

**Key insight:** Hybrid is not always faster. Ollama's scheduler splits layers based on available VRAM. On 8 GB dGPU + iGPU, Hybrid shines when a model barely exceeds one GPU's capacity. For small 7B models that fit entirely on the 6700S, **GPU-only mode often wins**.

Compare **Generation_tps** as your primary score. Use **TTFT_ms** for interactive/chat responsiveness. Use **VRAM_MB** to confirm the model is actually loaded on GPU(s).

---

## Laptop-Specific Notes

### Plugged In vs Battery

- **Plugged in + High Performance:** Highest, most reproducible scores
- **On battery:** CPU and GPU power limits drop sharply; dGPU may not boost fully
- Always note power state in your benchmark notes when comparing runs

### Dual-GPU Power Sharing

The 6900HX + 6700S shares a total system power budget (often 100–150 W combined). Running Hybrid loads **both** GPUs, which can cause each to throttle. Monitor temperatures with HWiNFO64 or AMD Software.

### MUX vs Hybrid Graphics

If your laptop supports a MUX switch (dGPU-only display path), GPU-only mode may perform slightly better due to reduced iGPU overhead. This toolkit does not change Windows graphics settings — only Ollama's Vulkan device selection.

---

## Radeon 680M iGPU Vulkan Workaround (Windows)

On Windows, **Vulkan is the supported GPU path** for the Radeon 680M. Two Windows-specific issues block the 680M by default:

1. **`OLLAMA_IGPU_ENABLE=1` is required** — Ollama detects the iGPU then **drops it** unless this is set (see `server.log`: `dropping integrated GPU; to enable, set OLLAMA_IGPU_ENABLE=1`).
2. Ollama's **bundled** `vulkan-1.dll` (1.4.321.1) often fails to enumerate AMD GPUs — rename it so the Windows system loader is used instead.

**Workaround:** rename Ollama's bundled loader so it falls back to the Windows system loader (`C:\Windows\System32\vulkan-1.dll`), which `vulkaninfo` already uses successfully.

Reference: [ollama/ollama#16677](https://github.com/ollama/ollama/issues/16677)

### Apply the workaround

```powershell
.\Ollama-VulkanWorkaround.ps1 -Enable -Force
.\Set-OllamaMode.ps1 APU
```

Or use the GUI: click **680M Fix**, then select **APU (680M)**.

**APU mode auto-applies this fix** when you use `Set-OllamaMode.ps1 APU` or the GUI APU card.

### What it changes

| Item | Before | After |
|------|--------|-------|
| Bundled DLL | `...\Ollama\lib\ollama\vulkan\vulkan-1.dll` | Renamed to `vulkan-1.dll.bak` |
| Loader used | Ollama bundled (broken for many AMD GPUs) | Windows system loader |
| Extra env vars | — | `OLLAMA_IGPU_ENABLE=1` (required — Ollama drops iGPUs without this), `OLLAMA_NUM_GPU=999` |

### Check status

```powershell
.\Ollama-VulkanWorkaround.ps1 -Status
```

### Restore bundled loader

```powershell
.\Ollama-VulkanWorkaround.ps1 -Disable -Force
```

### After Ollama updates

Ollama updates may restore the bundled `vulkan-1.dll`. Re-run `-Enable` if APU mode stops using the 680M (VRAM stays 0, speed matches CPU).

### Verify 680M is actually in use

1. Set APU mode and restart Ollama
2. Run `ollama run llama3.2:3b` with a short prompt
3. Task Manager → **GPU 1** (680M) should show 3D/compute activity
4. Benchmark: `.\Test-OllamaBenchmark.ps1 -ModelName "llama3.2:3b" -Mode APU -Force` — look for non-zero VRAM and faster tok/s than CPU

---

## Troubleshooting

### Wrong GPU Is Being Used

1. Run `.\Get-VulkanDevices.ps1` and verify **Vulkan** indices (not Task Manager labels)
2. Confirm persistent variables: `.\Ollama-AMD-Vulkan-Manager.ps1 -ShowStatus`
3. **Restart Ollama** after every change
4. Check Task Manager during inference:
   - dGPU mode: **GPU 0** (6700S) should show 3D utilization
   - APU mode: **GPU 1** (680M) should show activity

### Environment Variables Not Applying

- User-scope variables require a **new Ollama process** (quit tray app fully)
- Ollama started from Startup may have inherited old vars — quit and relaunch manually after first change
- Session-only (`-Scope Process`) changes apply only to processes started from that PowerShell session; the Ollama Windows app reads User environment at launch

### Hybrid Mode Quirks

- Layer splitting depends on reported VRAM; Vulkan may not expose exact VRAM without elevated capabilities
- If Hybrid is unstable or slower, fall back to **GPU-only** (`GGML_VK_VISIBLE_DEVICES=1`)
- Some iGPU Vulkan drivers are less stable under heavy LLM load — if you see crashes in Hybrid, use dGPU-only

### CPU Mode Still Uses GPU

- Ensure `OLLAMA_VULKAN=0` **and** `GGML_VK_VISIBLE_DEVICES=-1`
- Remove stale `ROCR_VISIBLE_DEVICES` or `CUDA_VISIBLE_DEVICES` entries
- Use `-RestoreBackup` if a previous configuration is interfering

### Benchmark Errors

| Error | Fix |
|-------|-----|
| `No local models found` | `ollama pull <model>` first |
| Connection refused | Start Ollama; verify `http://localhost:11434/api/tags` |
| Very slow / timeout | Model may be too large for VRAM; try a 7B Q4 model |
| VRAM_MB shows 0 | Normal if `/api/ps` has no loaded model entry; re-run after generation completes |

### vulkaninfo Not Found

Install the [Vulkan SDK](https://vulkan.lunarg.com/) or update AMD Adrenalin drivers. The tool also checks `%SystemRoot%\System32\vulkaninfo.exe`.

---

## File Layout

```
Ollama-AMD-Vulkan/
├── Ollama-AMD-Vulkan-Manager.ps1   # Mode switching (interactive + CLI)
├── Get-VulkanDevices.ps1           # Vulkan device discovery
├── Test-OllamaBenchmark.ps1        # API-based benchmarking
├── README.md                       # This file
└── ollama-benchmark-results.csv    # Created after first benchmark
```

Config and backups:

```
%USERPROFILE%\.ollama-amd-vulkan\
└── env-backup.json
```

---

## Safety Features

- **Confirmation prompts** before applying persistent changes (bypass with `-Force`)
- **Automatic backup** of User-scope variables before each persistent switch
- **Restore** via `-RestoreBackup` or interactive menu option 7
- **Ollama process detection** with restart guidance
- **Colored console output** for status, warnings, and errors

---

## References

- [Ollama GPU / Vulkan documentation](https://docs.ollama.com/gpu)
- [Ollama API reference](https://github.com/ollama/ollama/blob/main/docs/api.md)
- Environment variables: `OLLAMA_VULKAN`, `GGML_VK_VISIBLE_DEVICES`, `HIP_VISIBLE_DEVICES`