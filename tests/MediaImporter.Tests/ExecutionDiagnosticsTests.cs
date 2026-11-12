using MediaImporter;
using Npgsql;

public sealed class ExecutionDiagnosticsTests
{
    [Fact]
    public void Permanent_value_failures_and_transient_database_failures_have_safe_public_details()
    {
        const string secret = "Password=private-do-not-log;https://example.org/?key=private";
        foreach (var error in new Exception[] { new FormatException(secret), new OverflowException(secret),
            new InvalidDataException(secret), new ArgumentException(secret) })
        {
            var failure = ImportErrors.Classify(error);
            Assert.False(failure.Retryable);
            Assert.DoesNotContain("private", failure.ToString());
        }
        Assert.True(ImportErrors.Classify(new NpgsqlException(secret)).Retryable);
        Assert.True(ImportErrors.Classify(new AdapterReadException("http_503", true)).Retryable);
        Assert.False(ImportErrors.Classify(new AdapterReadException("missing_template_variable", false)).Retryable);
        Assert.False(ImportErrors.Classify(new PostgresException(secret, "ERROR", "ERROR", "22P02")).Retryable);
        Assert.True(ImportErrors.Classify(new PostgresException(secret, "ERROR", "ERROR", "40001")).Retryable);
        Assert.Equal("configuration_conflict", ImportErrors.Classify(new ConfigurationConflictException("current-revision")).Code);
        Assert.False(ImportErrors.Classify(new ConfigurationConflictException("current-revision")).Retryable);
        Assert.Equal("if_match_required", ImportErrors.Classify(new PreconditionRequiredException()).Code);
        Assert.False(ImportErrors.Classify(new PreconditionRequiredException()).Retryable);
    }

    [Fact]
    public void Idle_loops_are_healthy_and_failure_recovery_and_stopping_remain_visible()
    {
        var diagnostics = new ExecutionDiagnostics();
        diagnostics.Register("discovery");
        diagnostics.Tick("discovery");
        Assert.True(diagnostics.IsHealthy());
        diagnostics.Failure("discovery", new IOException("private secret"));
        Assert.False(diagnostics.IsHealthy());
        Assert.Equal("transport_unavailable", Assert.Single(diagnostics.Snapshot()).Error);
        diagnostics.Tick("discovery", "discovering", attemptId: Guid.NewGuid());
        diagnostics.Success("discovery", 123);
        Assert.True(diagnostics.IsHealthy());
        Assert.Equal(1, Assert.Single(diagnostics.Snapshot()).Failures);
        Assert.Equal(1, Assert.Single(diagnostics.Snapshot()).Completed);
        Assert.Equal("transport_unavailable", Assert.Single(diagnostics.Snapshot()).LastFailure!.Error.Code);
        diagnostics.Stopped("discovery");
        Assert.False(diagnostics.IsHealthy());
        Assert.DoesNotContain("secret", Assert.Single(diagnostics.Snapshot()).ToString());
    }
}
