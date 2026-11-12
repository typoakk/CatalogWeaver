using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace MediaImporter;

/// <summary>Initializes persistent service secrets before PostgreSQL or workers start.</summary>
public static class ServiceBootstrap
{
    public static async Task InitializeAsync(string directory, string? databasePassword = null,
        string? apiKey = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory);
        if (root == Path.GetPathRoot(root)) throw new InvalidDataException("Bootstrap requires a dedicated settings directory.");
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Settings directory cannot be a symbolic link.");
        var lockPath = Path.Combine(root, ".bootstrap.lock");
        FileStream? lease = null;
        for (var attempt = 0; lease is null; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt >= 100) throw new IOException("Another service bootstrap still owns the settings directory.");
            try { lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, ct); }
        }
        await using (lease)
        {
            Restrict(lockPath);
            await EnsureAsync(Path.Combine(root, "database-password"), databasePassword, 32, false, ct);
            await EnsureAsync(Path.Combine(root, "api-key"), apiKey, 48, true, ct);
        }
    }

    public static void ApplySecretFiles(IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration["Importer:ApiKey"])
            && configuration["Importer:ApiKeyFile"] is { Length: > 0 } keyFile)
            configuration["Importer:ApiKey"] = ReadSecret(keyFile, true);

        if (configuration["Importer:DatabasePasswordFile"] is { Length: > 0 } passwordFile)
        {
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("ImporterDb") ?? "");
            if (string.IsNullOrEmpty(connection.Password))
            {
                connection.Password = ReadSecret(passwordFile, false);
                configuration["ConnectionStrings:ImporterDb"] = connection.ConnectionString;
            }
        }
    }

    private static async Task EnsureAsync(string path, string? seed, int randomBytes, bool managementKey, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            var existing = ReadSecret(path, managementKey);
            if (!string.IsNullOrEmpty(seed) && !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(existing), Encoding.UTF8.GetBytes(seed)))
                throw new InvalidDataException("Configured bootstrap secret differs from its persisted value; existing secrets were preserved.");
            Restrict(path);
            return;
        }
        var value = string.IsNullOrEmpty(seed) ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(randomBytes)) : seed;
        Validate(value, managementKey);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Restrict(temporary);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(value + "\n"), ct);
                await stream.FlushAsync(ct);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
        }
        finally { File.Delete(temporary); }
    }

    private static string ReadSecret(string path, bool managementKey)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024)
            throw new InvalidDataException("Secret files must be small regular files.");
        var value = File.ReadAllText(path).TrimEnd('\r', '\n');
        Validate(value, managementKey);
        return value;
    }

    private static void Validate(string value, bool managementKey)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Contains('\r') || value.Contains('\n')
            || managementKey && (value.Length < 32 || value.Contains("GENERATE_", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid service secret; management keys require at least 32 characters.");
    }

    private static void Restrict(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
