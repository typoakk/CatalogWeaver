using System.Collections.Concurrent;

namespace MediaImporter;

public sealed record BackgroundLoopStatus(string Name, string State, string Stage, DateTime HeartbeatUtc,
    DateTime? LastSuccessUtc = null, string? Error = null, long Failures = 0, long Completed = 0,
    double? DurationMilliseconds = null, Guid? JobId = null, Guid? AttemptId = null,
    string? Provider = null, long? ExternalId = null)
{
    public BackgroundFailure? LastFailure { get; init; }
}

public sealed record BackgroundFailure(DateTime Utc, string Stage, ImportErrorInfo Error,
    Guid? JobId, Guid? AttemptId, string? Provider, long? ExternalId);

/// <summary>Small in-process liveness and progress registry; no exception messages or source payloads are retained.</summary>
public sealed class ExecutionDiagnostics
{
    private readonly ConcurrentDictionary<string, BackgroundLoopStatus> loops = new(StringComparer.Ordinal);

    public BackgroundLoopStatus[] Snapshot() => loops.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();

    public bool IsHealthy() => loops.Values.All(value => value.State is "starting" or "idle" or "running"
        && DateTime.UtcNow - value.HeartbeatUtc < TimeSpan.FromMinutes(2));

    public void Register(string name) => loops[name] = new(name, "starting", "starting", DateTime.UtcNow);

    public void Tick(string name, string stage = "idle", Guid? jobId = null, Guid? attemptId = null,
        string? provider = null, long? externalId = null)
    {
        loops.AddOrUpdate(name, _ => new(name, stage == "idle" ? "idle" : "running", stage, DateTime.UtcNow,
            JobId: jobId, AttemptId: attemptId, Provider: provider, ExternalId: externalId),
            (_, previous) => previous with { State = stage == "idle" ? "idle" : "running", Stage = stage,
                HeartbeatUtc = DateTime.UtcNow, Error = null, JobId = jobId, AttemptId = attemptId,
                Provider = provider, ExternalId = externalId });
    }

    public void Success(string name, double elapsedMilliseconds)
        => loops.AddOrUpdate(name, _ => new(name, "idle", "idle", DateTime.UtcNow, DateTime.UtcNow,
            Completed: 1, DurationMilliseconds: elapsedMilliseconds), (_, previous) => previous with
            { State = "idle", Stage = "idle", HeartbeatUtc = DateTime.UtcNow, LastSuccessUtc = DateTime.UtcNow,
                Error = null, Completed = previous.Completed + 1, DurationMilliseconds = elapsedMilliseconds,
                JobId = null, AttemptId = null, Provider = null, ExternalId = null });

    public void Failure(string name, Exception error, double? elapsedMilliseconds = null)
    {
        var failure = ImportErrors.Classify(error);
        loops.AddOrUpdate(name, _ => new(name, "degraded", "recovery", DateTime.UtcNow,
            Error: failure.Code, Failures: 1, DurationMilliseconds: elapsedMilliseconds)
            { LastFailure = new(DateTime.UtcNow, "recovery", failure, null, null, null, null) },
            (_, previous) => previous with { State = "degraded", HeartbeatUtc = DateTime.UtcNow,
                Error = failure.Code, Failures = previous.Failures + 1, DurationMilliseconds = elapsedMilliseconds,
                LastFailure = new(DateTime.UtcNow, previous.Stage, failure, previous.JobId, previous.AttemptId,
                    previous.Provider, previous.ExternalId) });
    }

    public void Stopped(string name) => loops.AddOrUpdate(name, _ => new(name, "stopped", "stopped", DateTime.UtcNow),
        (_, previous) => previous with { State = "stopped", HeartbeatUtc = DateTime.UtcNow });
}
