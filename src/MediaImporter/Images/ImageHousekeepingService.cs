namespace MediaImporter.Images;

public sealed class ImageHousekeepingService(ImagePipeline images, ILogger<ImageHousekeepingService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                var recovered = await images.RecoverStagingAsync(stoppingToken);
                if (recovered > 0)
                    logger.LogInformation("Reclaimed {Count} abandoned image staging directories.", recovered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogWarning("Image staging recovery deferred ({Error}).", error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
