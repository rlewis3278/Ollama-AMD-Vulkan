using OllamaToolkit.Core;
using OllamaToolkit.BenchmarkStore;
using OllamaToolkit.Core.EnvBackup;
using OllamaToolkit.Core.Modes;
using OllamaToolkit.Core.Ollama;
using OllamaToolkit.Core.Vulkan;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();
var commandArgs = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "set-mode" => await RunSetModeAsync(commandArgs),
        "vulkan-devices" => RunVulkanDevices(commandArgs),
        "status" => await RunStatusAsync(),
        "import-reports" => await RunImportReportsAsync(),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => Unknown(command)
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintUsage();
    return 1;
}

static int PrintUsage()
{
    Console.WriteLine("Ollama AMD Vulkan Toolkit CLI");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  set-mode <CPU|APU|GPU|Hybrid> [--no-restart]");
    Console.WriteLine("  vulkan-devices [--json]");
    Console.WriteLine("  status");
    Console.WriteLine("  import-reports");
    Console.WriteLine("  help");
    return 0;
}

static async Task<int> RunSetModeAsync(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: set-mode <CPU|APU|GPU|Hybrid> [--no-restart]");
        return 1;
    }

    var definitions = new ModeDefinitionService();
    if (!definitions.TryParse(args[0], out var mode))
    {
        Console.Error.WriteLine($"Invalid mode: {args[0]}");
        return 1;
    }

    var restart = !args.Contains("--no-restart", StringComparer.OrdinalIgnoreCase);
    var service = new ModeService(definitions);
    var result = await service.ApplyModeAsync(mode, restartOllama: restart).ConfigureAwait(false);

    Console.WriteLine($"Applied mode: {result.Mode}");
    Console.WriteLine($"Restarted Ollama: {result.RestartedOllama}");
    return 0;
}

static int RunVulkanDevices(string[] args)
{
    var discovery = new VulkanDeviceDiscovery();
    var map = discovery.Discover();
    var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

    if (json)
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(map, JsonFileHelper.Options));
        return 0;
    }

    Console.WriteLine($"APU Vulkan index: {map.ApuVulkanIndex} ({map.ApuName})");
    Console.WriteLine($"GPU Vulkan index: {map.GpuVulkanIndex} ({map.GpuName})");
    Console.WriteLine($"Hybrid value:     {map.HybridVulkanValue}");
    Console.WriteLine(map.TaskManagerNote);
    return 0;
}

static async Task<int> RunStatusAsync()
{
    var modeService = new ModeService();
    var api = new OllamaApiClient();
    var env = new EnvBackupService();

    Console.WriteLine($"Detected mode: {modeService.DetectCurrentMode()}");
    Console.WriteLine($"Ollama API:    {(await api.IsReadyAsync().ConfigureAwait(false) ? "ready" : "not reachable")}");
    Console.WriteLine();
    Console.WriteLine("Managed environment variables (User scope):");
    foreach (var entry in env.ReadUserSnapshot().OrderBy(k => k.Key))
    {
        Console.WriteLine($"  {entry.Key} = {entry.Value ?? "(not set)"}");
    }

    return 0;
}

static async Task<int> RunImportReportsAsync()
{
    var importer = new ReportImporter();
    var count = await importer.ImportReportsAsync().ConfigureAwait(false);
    Console.WriteLine($"Imported {count} report(s).");
    return 0;
}