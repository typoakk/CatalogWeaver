using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace CatalogWeaver.MonitorClient;

public sealed record SshTarget(string Host, int ApiPort = 8770);
public sealed record EnvironmentInput(string Name, string? Address, string? Key, string Color = "#5dbaa6",
    string Description = "", int IntervalSeconds = 5, bool Enabled = true, int TimeoutSeconds = 8,
    long MinimumFreeBytes = 5368709120, int NoProgressMinutes = 15, SshTarget? Ssh = null);
public sealed record EnvironmentConnection(Guid Id, string Name, string? Address, string ProtectedKey, Guid ServerId,
    string Color, string Description, int IntervalSeconds, bool Enabled, int TimeoutSeconds, long MinimumFreeBytes,
    int NoProgressMinutes, SshTarget? Ssh = null);

/// <summary>PC-local profiles. Source credentials never belong to an importer server.</summary>
public sealed class ConnectionRegistry : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly FileStream processLock;
    private EnvironmentConnection[] rows = [];
    public string DirectoryPath { get; }
    private string FilePath => Path.Combine(DirectoryPath, "environments.json");

    public static string DataDirectory(IConfiguration configuration)
        => Path.GetFullPath(configuration["Client:DataDirectory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatalogWeaver", "MonitorClient"));

    public ConnectionRegistry(IConfiguration configuration, IDataProtectionProvider protection)
    {
        DirectoryPath = DataDirectory(configuration);
        if (DirectoryPath == Path.GetPathRoot(DirectoryPath)) throw new InvalidOperationException("Choose a dedicated client settings directory.");
        Directory.CreateDirectory(DirectoryPath);
        Restrict(DirectoryPath, true);
        protector = protection.CreateProtector("CatalogWeaver.MonitorClient.Connections.v1");
        processLock = new FileStream(Path.Combine(DirectoryPath, ".client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Restrict(processLock.Name, false);
        if (File.Exists(FilePath))
        {
            try
            {
                var document = JsonSerializer.Deserialize<RegistryDocument>(File.ReadAllText(FilePath), Json);
                if (document?.Version != 1 || document.Environments is null) throw new InvalidDataException();
                rows = document.Environments;
                if (rows.Length > 100 || rows.Select(x => x.Id).Distinct().Count() != rows.Length
                    || rows.Select(x => x.ServerId).Distinct().Count() != rows.Length) throw new InvalidDataException();
                foreach (var row in rows) Validate(new(row.Name,row.Address,null,row.Color,row.Description,row.IntervalSeconds,row.Enabled,
                    row.TimeoutSeconds,row.MinimumFreeBytes,row.NoProgressMinutes,row.Ssh));
            }
            catch { processLock.Dispose(); throw new InvalidDataException("Client settings cannot be loaded. Existing files were preserved."); }
        }
    }
    public EnvironmentConnection[] List() => Volatile.Read(ref rows).ToArray();
    public EnvironmentConnection Get(Guid id) => List().SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
    public string Key(EnvironmentConnection connection) => protector.Unprotect(connection.ProtectedKey);
    public static Uri Address(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0
            || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Use the server HTTP(S) origin without a path, credentials or query.");
        return uri;
    }
    public static void Validate(EnvironmentInput input)
    {
        if (input.Ssh is null)
        {
            _ = Address(input.Address);
        }
        else if (!string.IsNullOrWhiteSpace(input.Address) || input.Ssh.ApiPort is < 1 or > 65535
            || input.Ssh.Host is not { Length: > 0 and <= 255 }
            || !Regex.IsMatch(input.Ssh.Host, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]*(?:@[a-zA-Z0-9][a-zA-Z0-9_.-]*)?\z"))
        {
            throw new ArgumentException("Use an SSH host alias or user@host and a valid API port.");
        }
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 80 || input.Description is null || input.Description.Length > 500
            || input.IntervalSeconds is < 2 or > 300 || input.TimeoutSeconds is < 1 or > 60 || input.MinimumFreeBytes is < 0 or > 1_125_899_906_842_624
            || input.NoProgressMinutes is < 1 or > 1440 || input.Color is null || !Regex.IsMatch(input.Color, "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("Invalid environment fields.");
    }
    public async Task SaveAsync(Guid id, EnvironmentInput input, Guid serverId, string key, bool create, CancellationToken ct)
    {
        Validate(input);
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 32 or > 4096 || key.Contains('\r') || key.Contains('\n'))
            throw new ArgumentException("Invalid monitoring key.");
        await writer.WaitAsync(ct);
        try
        {
            var current = List(); var existing = current.SingleOrDefault(x => x.Id == id);
            if (!create && existing is null) throw new KeyNotFoundException();
            if (existing is not null && existing.ServerId != serverId) throw new ClientConnectionException("server_identity_changed");
            if (current.Any(x => x.Id != id && x.ServerId == serverId)) throw new ClientConnectionException("server_already_connected");
            if (create && current.Length >= 100) throw new ArgumentException("At most 100 connections are supported.");
            var address = input.Ssh is null ? Address(input.Address).AbsoluteUri : null;
            var updated = current.Where(x => x.Id != id).Append(new EnvironmentConnection(id,input.Name.Trim(),address,
                protector.Protect(key),serverId,input.Color,input.Description,input.IntervalSeconds,input.Enabled,input.TimeoutSeconds,
                input.MinimumFreeBytes,input.NoProgressMinutes,input.Ssh)).OrderBy(x => x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
            await PersistAsync(updated,ct);
        }
        finally { writer.Release(); }
    }
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await writer.WaitAsync(ct);
        try { await PersistAsync(List().Where(x => x.Id != id).ToArray(),ct); }
        finally { writer.Release(); }
    }
    private async Task PersistAsync(EnvironmentConnection[] updated,CancellationToken ct)
    {
        var temporary = Path.Combine(DirectoryPath,"environments."+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            await using(var stream = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                Restrict(temporary,false);
                await JsonSerializer.SerializeAsync(stream,new RegistryDocument(1,updated),Json,ct);
                await stream.FlushAsync(ct); stream.Flush(true);
            }
            File.Move(temporary,FilePath,true); Volatile.Write(ref rows,updated);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Restrict(string path,bool directory)
    {
        if(!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|(directory?UnixFileMode.UserExecute:0));
    }
    public void Dispose() { processLock.Dispose(); writer.Dispose(); }
    private sealed record RegistryDocument(int Version,EnvironmentConnection[] Environments);
}

public sealed class ClientConnectionException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
