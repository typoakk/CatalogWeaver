namespace MediaImporter;

/// <summary>An optimistic configuration write used a revision that is no longer current.</summary>
public sealed class ConfigurationConflictException(string? currentVersion)
    : Exception("Configuration changed. Read the current revision before saving.")
{
    public string? CurrentVersion { get; } = currentVersion;

    internal static void Check(string? expectedVersion, string? currentVersion)
    {
        if (expectedVersion is not null && (expectedVersion == "" ? currentVersion is not null : expectedVersion == "*" ? currentVersion is null
            : !string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal)))
            throw new ConfigurationConflictException(currentVersion);
    }
}
