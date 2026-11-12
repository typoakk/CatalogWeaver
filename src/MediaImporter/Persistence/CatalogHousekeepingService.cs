namespace MediaImporter;

public sealed class CatalogHousekeepingService(IServiceScopeFactory scopes,
    ILogger<CatalogHousekeepingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<IImportPersistence>()
                    .SweepOrphansAsync(stoppingToken);
                if (removed > 0) logger.LogInformation("Reclaimed {Count} unreferenced importer files.", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Data.Common.DbException)
            {
                logger.LogWarning("Importer file cleanup deferred ({ErrorType})", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
