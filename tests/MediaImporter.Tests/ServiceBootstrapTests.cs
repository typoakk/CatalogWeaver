using Microsoft.Extensions.Configuration;
using Npgsql;

namespace MediaImporter.Tests;

public sealed class ServiceBootstrapTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "catalogweaver-bootstrap-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task First_bootstrap_creates_private_secrets_without_a_database_or_dotenv()
    {
        await ServiceBootstrap.InitializeAsync(root);
        var password = await File.ReadAllTextAsync(Path.Combine(root, "database-password"));
        var key = await File.ReadAllTextAsync(Path.Combine(root, "api-key"));
        Assert.Equal(64, password.TrimEnd().Length);
        Assert.Equal(96, key.TrimEnd().Length);
        Assert.NotEqual(password, key);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "database-password")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, "api-key")));
        }
    }

    [Fact]
    public async Task Repeated_and_concurrent_bootstraps_keep_the_same_secrets()
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ServiceBootstrap.InitializeAsync(root)));
        var password = await File.ReadAllTextAsync(Path.Combine(root, "database-password"));
        var key = await File.ReadAllTextAsync(Path.Combine(root, "api-key"));
        await ServiceBootstrap.InitializeAsync(root);
        Assert.Equal(password, await File.ReadAllTextAsync(Path.Combine(root, "database-password")));
        Assert.Equal(key, await File.ReadAllTextAsync(Path.Combine(root, "api-key")));
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task Explicit_initial_values_are_preserved_and_changed_seeds_are_rejected()
    {
        const string key = "TestOnlyManagementKeyWithAtLeast32Characters";
        await ServiceBootstrap.InitializeAsync(root, "test-only-password", key);
        await ServiceBootstrap.InitializeAsync(root, "test-only-password", key);
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceBootstrap.InitializeAsync(root, "different-test-password", key));
        Assert.Equal("test-only-password", (await File.ReadAllTextAsync(Path.Combine(root, "database-password"))).TrimEnd());
        Assert.Equal(key, (await File.ReadAllTextAsync(Path.Combine(root, "api-key"))).TrimEnd());
    }

    [Fact]
    public async Task File_settings_supply_missing_credentials_and_preserve_connection_options()
    {
        await ServiceBootstrap.InitializeAsync(root, "test;password=with-special-characters", "TestOnlyManagementKeyWithAtLeast32Characters");
        var configuration = Configuration("Host=state;Database=example_test;Username=importer;Timeout=17");
        ServiceBootstrap.ApplySecretFiles(configuration);
        Assert.Equal("TestOnlyManagementKeyWithAtLeast32Characters", configuration["Importer:ApiKey"]);
        var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("ImporterDb"));
        Assert.Equal("test;password=with-special-characters", connection.Password);
        Assert.Equal("example_test", connection.Database);
        Assert.Equal(17, connection.Timeout);
    }

    [Fact]
    public void Explicit_credentials_do_not_require_secret_files()
    {
        var configuration = Configuration("Host=state;Database=example_test;Username=importer;Password=explicit-test-password");
        configuration["Importer:ApiKey"] = "ExplicitTestOnlyManagementKeyAtLeast32Characters";
        ServiceBootstrap.ApplySecretFiles(configuration);
        Assert.Equal("ExplicitTestOnlyManagementKeyAtLeast32Characters", configuration["Importer:ApiKey"]);
        Assert.Equal("explicit-test-password", new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("ImporterDb")).Password);
    }

    [Fact]
    public async Task Corrupt_persisted_secret_is_not_silently_replaced()
    {
        await ServiceBootstrap.InitializeAsync(root);
        await File.WriteAllTextAsync(Path.Combine(root, "api-key"), "short");
        await Assert.ThrowsAsync<InvalidDataException>(() => ServiceBootstrap.InitializeAsync(root));
        Assert.Equal("short", await File.ReadAllTextAsync(Path.Combine(root, "api-key")));
    }

    private IConfigurationRoot Configuration(string connection) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:ImporterDb"] = connection,
            ["Importer:ApiKeyFile"] = Path.Combine(root, "api-key"),
            ["Importer:DatabasePasswordFile"] = Path.Combine(root, "database-password")
        }).Build();

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
