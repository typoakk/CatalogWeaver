namespace MediaImporter;

/// <summary>One shared budget for item and discovery previews, independent of background job slots.</summary>
public sealed class PreviewExecutor(ImporterOptions options) : IDisposable
{
    private readonly SemaphoreSlim slots = new(options.PreviewCount, options.PreviewCount);
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(options.PreviewTimeoutSeconds));
        var acquired = false;
        try
        {
            await slots.WaitAsync(budget.Token);
            acquired = true;
            return await action(budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AdapterReadException("preview_timeout", true); }
        finally { if (acquired) slots.Release(); }
    }
    public void Dispose() => slots.Dispose();
}
