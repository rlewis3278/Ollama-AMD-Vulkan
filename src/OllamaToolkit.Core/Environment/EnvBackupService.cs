namespace OllamaToolkit.Core.EnvBackup;

public sealed class EnvBackupService
{
    public async Task SaveBackupAsync(CancellationToken cancellationToken = default)
    {
        ConfigPaths.EnsureConfigDirectory();
        var snapshot = ReadUserSnapshot();
        var payload = new EnvBackupDocument
        {
            Timestamp = DateTimeOffset.Now.ToString("o"),
            Variables = snapshot
        };

        await JsonFileHelper.WriteAsync(ConfigPaths.EnvBackupFile, payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = await JsonFileHelper.ReadAsync<EnvBackupDocument>(ConfigPaths.EnvBackupFile, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("No env-backup.json found.");

        foreach (var entry in backup.Variables)
        {
            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                System.Environment.SetEnvironmentVariable(entry.Key, null, EnvironmentVariableTarget.User);
            }
            else
            {
                System.Environment.SetEnvironmentVariable(entry.Key, entry.Value, EnvironmentVariableTarget.User);
            }
        }
    }

    public Dictionary<string, string?> ReadUserSnapshot()
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ConfigPaths.ManagedEnvironmentVariables)
        {
            snapshot[name] = System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        }

        return snapshot;
    }

    public sealed class EnvBackupDocument
    {
        public string Timestamp { get; set; } = string.Empty;
        public Dictionary<string, string?> Variables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}