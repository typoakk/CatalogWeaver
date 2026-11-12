using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ImageMagick;

namespace MediaImporter.Images;

public sealed record EncoderModuleDefinition
{
    public int ConfigVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Driver { get; init; } = "magick";
    public string Format { get; init; } = "";
    public bool RejectAlpha { get; init; }
    public Dictionary<string, string> Defines { get; init; } = [];
    public EncoderProfileDefinition[] Profiles { get; init; } = [];
    public EncoderQualityRange? QualityRange { get; init; }
}

public sealed record EncoderProfileDefinition
{
    public string Name { get; init; } = "";
    public int? Quality { get; init; }
    public Dictionary<string, string> Defines { get; init; } = [];
}

public sealed record EncoderQualityRange(string Prefix, int Minimum = 1, int Maximum = 100);

/// <summary>A self-contained instruction captured in the queued adapter revision.</summary>
public sealed record ImageEncodingProfile(string Name, string Module, string Driver,
    string Format, string ContentType, int? Quality, Dictionary<string, string> Defines, string Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RejectAlpha = false)
{
    public static bool ValidName(string? value) => value is not null
        && Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant);

    public static string Hash(ImageEncodingProfile profile) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile with { Version = "" }, AdapterRegistry.Json))));

    public static string ContentTypeFor(string format)
    {
        if (!Enum.TryParse<MagickFormat>(format, false, out var parsed) || parsed.ToString() != format)
            throw new InvalidDataException("Encoder format must be an exact MagickFormat name.");
        var information = MagickNET.SupportedFormats.FirstOrDefault(value => value.Format == parsed);
        if (information is not { SupportsReading: true, SupportsWriting: true, MimeType: { } mime }
            || !mime.StartsWith("image/", StringComparison.Ordinal))
            throw new InvalidDataException("Encoder format must support image reading and writing in this runtime.");
        return mime;
    }

    public static void Validate(ImageEncodingProfile profile)
    {
        if (!ValidName(profile.Name) || profile.Name == "original" || !AdapterRegistry.ValidProvider(profile.Module)
            || profile.Driver != "magick" || profile.Quality is < 1 or > 100
            || profile.ContentType != ContentTypeFor(profile.Format))
            throw new InvalidDataException("Invalid image encoding profile.");
        ValidateDefines(profile.Defines);
        if (profile.Version != Hash(profile))
            throw new InvalidDataException("Image encoding profile version does not match its instructions.");
    }

    internal static void ValidateDefines(Dictionary<string, string> defines)
    {
        if (defines is null || defines.Count > 32 || defines.Any(pair => pair.Value is null || pair.Value.Length > 512
            || !Regex.IsMatch(pair.Key, "^[a-z0-9-]+:[a-z0-9-]+$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Invalid encoder defines; use format:option names and short string values.");
    }
}

public sealed record EncoderRegistration(string Id, string DisplayName, string? Version,
    string[] Profiles, string[] Errors, DateTime CheckedUtc);
