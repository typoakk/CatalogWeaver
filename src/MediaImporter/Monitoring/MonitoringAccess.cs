using System.Security.Cryptography;
using System.Text;

namespace MediaImporter.Monitoring;

/// <summary>The server grants read access to its own monitoring data only.</summary>
public sealed class MonitoringAccess(ImporterOptions options, IConfiguration configuration)
{
    public string ReadKey => configuration["Monitoring:ApiKey"] is { Length: >= 32 } configured
        ? configured : Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey),
            Encoding.UTF8.GetBytes("CatalogWeaver/monitor/read/v1")));

    public bool CanRead(HttpContext context) => HasToken(context, ReadKey);
    public bool CanManage(HttpContext context) => HasToken(context, options.ApiKey);
    private static bool HasToken(HttpContext context, string expected)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }
}
