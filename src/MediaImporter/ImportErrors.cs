using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace MediaImporter;

public sealed record ImportErrorInfo(string Code, bool Retryable, string SafeMessage, string? SqlState = null);

/// <summary>Public diagnostics never contain request URLs, credentials, SQL values or raw exception messages.</summary>
public static partial class ImportErrors
{
    public static ImportErrorInfo Classify(Exception error)
    {
        if (error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count == 1)
            return Classify(aggregate.Flatten().InnerExceptions[0]);
        if (error is AdapterReadException adapter)
            return new(SafeCode().IsMatch(adapter.Code) ? adapter.Code : "adapter_error", adapter.Retryable,
                adapter.Retryable ? "The source request could not be completed; retry is scheduled." : "The source or adapter rejected this operation.");
        if (error is PostgresException postgres)
        {
            var transient = postgres.SqlState.StartsWith("08", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("40", StringComparison.Ordinal)
                || postgres.SqlState.StartsWith("53", StringComparison.Ordinal)
                || postgres.SqlState is "55P03" or "57014" or "57P01" or "57P02" or "57P03";
            return new("database_" + postgres.SqlState, transient,
                transient ? "The database is temporarily unavailable or busy." : "The database rejected the configured operation or value.", postgres.SqlState);
        }
        return error switch
        {
            MediaImporter.Images.ProcessDeadlineException => new("image_timeout", true, "The native image worker exceeded its execution deadline."),
            MediaImporter.Images.ImageStorageFullException => new("storage_full", true, "The media storage budget or free-space reserve was reached."),
            ConfigurationConflictException => new("configuration_conflict", false, "Read the current configuration revision before applying your changes."),
            PreconditionRequiredException => new("if_match_required", false, "Supply the configuration revision using If-Match before saving."),
            FormatException or OverflowException => new("invalid_value", false, "A value cannot be converted to the configured target type."),
            JsonException => new("invalid_json", false, "The supplied JSON does not match the contract."),
            InvalidDataException => new("invalid_data", false, "The configuration or data failed validation."),
            ArgumentException => new("invalid_argument", false, "An operation argument is invalid."),
            KeyNotFoundException => new("not_found", false, "The requested configuration or object does not exist."),
            InvalidOperationException or NotSupportedException => new("invalid_operation", false, "The configured operation is unavailable or unsupported."),
            NpgsqlException => new("database_unavailable", true, "The database connection could not complete the operation."),
            TimeoutException => new("operation_timeout", true, "The operation exceeded its time budget."),
            OperationCanceledException => new("operation_cancelled", true, "The operation was cancelled or lost its lease."),
            IOException or HttpRequestException => new("transport_unavailable", true, "A file or network operation could not be completed."),
            _ => new("unexpected_error", true, "An unexpected internal error interrupted the operation.")
        };
    }

    [GeneratedRegex("\\A[a-zA-Z0-9_.:-]{1,96}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCode();
}
