namespace MediaImporter;

public sealed class ImporterOptions
{
    public string AdaptersPath { get; set; } = "adapters";
    public string EncodersPath { get; set; } = "encoders";
    public string MediaRoot { get; set; } = "/data/media";
    public string ApiKey { get; set; } = "";
    public string PersistenceMode { get; set; } = "Standalone";
    public int WorkerCount { get; set; } = 2;
    public bool WorkersEnabled { get; set; } = true;
    public int MaximumAttempts { get; set; } = 16;
    public int LeaseSeconds { get; set; } = 180;
    public int ImportTimeoutSeconds { get; set; } = 1800;
    public int RefreshHours { get; set; } = 24;
    public int PreviewCount { get; set; } = 2;
    public int PreviewTimeoutSeconds { get; set; } = 120;

    public void Validate()
    {
        if (PersistenceMode != "Standalone")
            throw new InvalidOperationException("Use Standalone mode and configure destinations.database; application-specific persistence modes have been replaced by JSON profiles.");
        if (string.IsNullOrWhiteSpace(AdaptersPath) || string.IsNullOrWhiteSpace(EncodersPath) || !Path.IsPathFullyQualified(MediaRoot))
            throw new InvalidOperationException("Configure Importer:AdaptersPath, Importer:EncodersPath and an absolute Importer:MediaRoot.");
        if (string.IsNullOrWhiteSpace(ApiKey) || ApiKey.Length < 32 || ApiKey.Contains("GENERATE_", StringComparison.Ordinal))
            throw new InvalidOperationException("Set Importer:ApiKey to a private value of at least 32 characters.");
        if (WorkerCount is < 1 or > 16 || MaximumAttempts is < 1 or > 32
            || LeaseSeconds is < 30 or > 3600 || ImportTimeoutSeconds is < 30 or > 86400
            || RefreshHours is < 1 or > 8760 || PreviewCount is < 1 or > 16
            || PreviewTimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException("Invalid importer worker, retry, lease or scheduling settings.");
    }
}
