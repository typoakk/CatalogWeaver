using MediaImporter.Images;

namespace MediaImporter;

public sealed class ImportOrchestrator(AdapterRuntime runtime, AdapterTransport transport,
    ImagePipeline images, ImageStorageOptions imageOptions, IImportPersistence persistence, ILogger<ImportOrchestrator> logger)
{
    public async Task<ImportResult> ExecuteAsync(AdapterSnapshot adapter, long externalId, bool preview,
        bool processPreviewImages, Func<string, CancellationToken, Task>? progress, CancellationToken ct,
        ImportJob? job = null, bool planDatabase = false)
    {
        if (progress is not null) await progress("metadata", ct);
        var item = await runtime.ReadItemAsync(adapter, externalId, ct);
        var assets = new List<AssetResult>();
        var prepared = new List<PreparedImage>();
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>(item.Warnings);
        PersistenceResult? saved = null;
        ImportPreviewPlan? plan = null;
        try
        {
            foreach (var asset in item.Assets)
            {
                ct.ThrowIfCancellationRequested();
                var position = positions.GetValueOrDefault(asset.Role);
                positions[asset.Role] = position + 1;
                var result = new AssetResult
                {
                    Role = asset.Role, Position = position, SourceUrl = asset.SourceUrl,
                    Title = asset.Title, Storage = asset.Storage,
                    RequestedProfile = asset.ImageProfile,
                    State = preview && asset.Storage == "local" && !processPreviewImages ? "planned" : "remote"
                };
                if (asset.Storage == "local" && (!preview || processPreviewImages))
                {
                    if (progress is not null) await progress("assets:" + asset.Role, ct);
                    try
                    {
                        var selectedUrl = asset.SourceUrl;
                        var image = await images.PrepareAsync(adapter.Definition.Destinations.Files.Directory,
                            asset.ImageProfile ?? "original", async token =>
                            {
                                var downloaded = await DownloadCandidateAsync(adapter, asset, token);
                                selectedUrl = downloaded.Url;
                                return downloaded.Bytes;
                            }, ct, adapter.Definition.AssetPolicies.GetValueOrDefault(asset.Role)?.ResolvedImageProfile);
                        prepared.Add(image);
                        var published = preview ? null : await images.PublishAsync(image, ct);
                        result = result with
                        {
                            SourceUrl = selectedUrl, State = "stored",
                            Hash = image.Hash, ContentType = image.ContentType, Bytes = image.Bytes,
                            RelativePath = published?.RelativePath ?? Path.GetRelativePath(Path.GetFullPath(imageOptions.MediaRoot),
                                Path.Combine(image.DestinationDirectory, image.Hash)).Replace(Path.DirectorySeparatorChar, '/'),
                            ActualProfile = image.ActualProfile,
                            Warnings = image.Warnings
                        };
                        warnings.AddRange(image.Warnings);
                    }
                    catch (AdapterReadException error) when (error.HttpStatusCode is 404 or 410)
                    {
                        result = result with { State = "missing", Error = error.Code };
                        warnings.Add("asset_missing:" + asset.Role);
                    }
                    catch (Exception error) when (error is IOException or InvalidDataException or AdapterReadException
                        or ImageMagick.MagickException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException
                        || error is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        result = result with { State = "retry", Error = error.GetType().Name };
                        warnings.Add("asset_retry:" + asset.Role + ":" + error.GetType().Name);
                    }
                }
                assets.Add(result);
            }

            var partial = item.AssetStatus.Values.Contains("retry", StringComparer.Ordinal)
                || assets.Any(asset => asset.State == "retry")
                || item.Warnings.Any(warning => warning.StartsWith("supplement:", StringComparison.Ordinal));
            if (preview && planDatabase)
                plan = await persistence.PlanAsync(item, assets, adapter.Version, ct,
                    adapter.Definition.Destinations.Database);
            if (!preview)
            {
                if (progress is not null) await progress("saving", ct);
                saved = await persistence.SaveAsync(item, assets, adapter.Version, ct, job,
                    adapter.Definition.Destinations.Database);
                // A committed card no longer needs temporary originals. Cleanup errors are retriable housekeeping.
                foreach (var image in prepared)
                {
                    try { await images.CompleteAsync(image, CancellationToken.None); }
                    catch (IOException error)
                    {
                        logger.LogWarning("Staging cleanup deferred ({ErrorType})", error.GetType().Name);
                        warnings.Add("staging_cleanup_deferred");
                    }
                }
                try { await persistence.CleanupAsync(saved, CancellationToken.None); }
                catch (Exception error) when (error is IOException or System.Data.Common.DbException)
                {
                    logger.LogWarning("Replaced image cleanup deferred ({ErrorType})", error.GetType().Name);
                    warnings.Add("image_cleanup_deferred");
                }
            }
            return new ImportResult
            {
                Mode = preview ? "preview" : "import", Provider = item.Provider, ExternalId = item.ExternalId,
                MediaId = saved?.MediaId ?? plan?.MediaId, AdapterVersion = adapter.Version, Status = partial ? "partial" : "completed",
                Item = saved is null ? item : item with { AssetStatus = saved.AssetStatus },
                Assets = saved?.Assets ?? assets.ToArray(), Warnings = warnings.Concat(plan?.Warnings ?? []).Distinct().ToArray(),
                Plan = plan
            };
        }
        finally
        {
            if (preview)
                foreach (var image in prepared) await images.DiscardAsync(image, CancellationToken.None);
            else if (saved is null)
                foreach (var image in prepared) await images.ReleaseAsync(image, CancellationToken.None);
        }
    }

    private async Task<(string Url, byte[] Bytes)> DownloadCandidateAsync(AdapterSnapshot adapter,
        AdapterAsset asset, CancellationToken ct)
    {
        AdapterReadException? missing = null;
        foreach (var url in new[] { asset.SourceUrl }.Concat(asset.Alternatives).Distinct(StringComparer.Ordinal))
        {
            try { return (url, await transport.DownloadImageAsync(adapter, url, ct)); }
            catch (AdapterReadException error) when (error.HttpStatusCode is 404 or 410) { missing = error; }
        }
        if (missing is not null) throw missing;
        throw new InvalidDataException("No image candidate is available.");
    }
}
