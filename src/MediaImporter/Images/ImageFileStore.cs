using System.Security.Cryptography;
using System.Text.Json;

namespace MediaImporter.Images;

internal sealed class ImageFileStore
{
    private readonly ImageStorageOptions options;
    private readonly string root;
    private readonly string stagingRoot;
    private readonly StringComparison pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private bool reconciled;

    private sealed record Inventory(int Version, long Bytes);

    internal ImageFileStore(ImageStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.MediaRoot) || options.CoverBudgetBytes < 1
            || options.MinimumFreeBytes < 0 || options.MaximumImageBytes < 1
            || options.MaximumEncodedBytes < 1 || options.StagingRetentionHours < 1)
            throw new InvalidDataException("Invalid image storage configuration.");
        this.options = options;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.MediaRoot));
        if (root == Path.GetPathRoot(root))
            throw new InvalidDataException("Image storage must use a dedicated directory.");
        stagingRoot = Path.Combine(root, ".staging");
        Directory.CreateDirectory(root);
    }

    internal string ResolveDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidDataException("An image destination directory is required.");
        var resolved = Path.GetFullPath(Path.IsPathRooted(destination) ? destination : Path.Combine(root, destination));
        EnsureWithinRoot(resolved);
        if (resolved.Equals(stagingRoot, pathComparison)
            || resolved.StartsWith(stagingRoot + Path.DirectorySeparatorChar, pathComparison))
            throw new InvalidDataException("Image destination cannot use the staging directory.");
        CheckExistingDirectories(resolved);
        return resolved;
    }

    internal string CreateWorkingDirectory()
    {
        CheckExistingDirectories(stagingRoot);
        var directory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal void RemoveWorkingDirectory(string directory)
    {
        var resolved = Path.GetFullPath(directory);
        if (!Path.GetDirectoryName(resolved)!.Equals(stagingRoot, pathComparison)
            || !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            throw new InvalidDataException("Invalid staging directory.");
        CheckExistingDirectories(resolved);
        if (!Directory.Exists(resolved))
            return;
        foreach (var file in Directory.EnumerateFiles(resolved))
            File.Delete(file);
        Directory.Delete(resolved);
    }

    internal FileStream AcquireWorkingLease(string directory)
        => new(Path.Combine(directory, ".lease"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    internal Task<int> RecoverStagingAsync(TimeSpan minimumAge, CancellationToken cancellationToken)
    {
        if (minimumAge < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        CheckExistingDirectories(stagingRoot);
        if (!Directory.Exists(stagingRoot))
            return Task.FromResult(0);
        var removed = 0;
        var cutoff = DateTime.UtcNow - minimumAge;
        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)
                || Directory.GetLastWriteTimeUtc(directory) > cutoff)
                continue;
            try
            {
                CheckExistingDirectories(directory);
                var leasePath = Path.Combine(directory, ".lease");
                using (var lease = AcquireWorkingLease(directory))
                    foreach (var file in Directory.EnumerateFiles(directory).Where(path => path != leasePath))
                        File.Delete(file);
                File.Delete(leasePath);
                Directory.Delete(directory);
                removed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Active attempts own the lease; another recovery pass can retry abandoned ones.
            }
        }
        return Task.FromResult(removed);
    }

    internal async Task DeletePublishedAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException("Published image path must be relative to the media root.");
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        EnsureWithinRoot(path);
        if (!IsHash(Path.GetFileName(path)))
            throw new InvalidDataException("Published image path must end in its hash.");
        CheckExistingDirectories(Path.GetDirectoryName(path)!);
        await using var lease = await LockAsync(cancellationToken);
        if (!File.Exists(path))
            return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Published image cannot be a symbolic link.");
        var inventory = await ReadInventoryAsync(cancellationToken);
        var bytes = new FileInfo(path).Length;
        await MarkDirtyAsync(cancellationToken);
        File.Delete(path);
        await WriteInventoryAsync(new Inventory(1, Math.Max(0, inventory.Bytes - bytes)), CancellationToken.None);
    }

    internal void CheckFreeSpace(long additionalBytes)
    {
        if (new DriveInfo(root).AvailableFreeSpace - additionalBytes < options.MinimumFreeBytes)
            throw new ImageStorageFullException("Image storage free-space reserve reached.");
    }

    internal async Task<PublishedImage> PublishAsync(PreparedImage prepared, CancellationToken cancellationToken)
    {
        var destination = ResolveDestination(prepared.DestinationDirectory);
        if (!IsHash(prepared.Hash))
            throw new InvalidDataException("Invalid image hash.");
        CheckStagedImage(prepared.SelectedPath, prepared.WorkingDirectory);
        var target = Path.Combine(destination, prepared.Hash);
        await using var lease = await LockAsync(cancellationToken);
        var usage = await ReadInventoryAsync(cancellationToken);
        var oldSize = 0L;
        if (File.Exists(target))
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Published image cannot be a symbolic link.");
            await using var existing = File.OpenRead(target);
            oldSize = existing.Length;
            var existingHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(existing, cancellationToken));
            if (existingHash == prepared.Hash)
                return Describe(prepared, target);
        }

        await using (var selected = File.OpenRead(prepared.SelectedPath))
        {
            if (selected.Length != prepared.Bytes || selected.Length is < 1
                || selected.Length > Math.Max(options.MaximumImageBytes, options.MaximumEncodedBytes))
                throw new InvalidDataException("Staged image length changed before publication.");
            var selectedHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(selected, cancellationToken));
            if (selectedHash != prepared.Hash)
                throw new InvalidDataException("Staged image hash changed before publication.");
        }
        if (usage.Bytes - oldSize + prepared.Bytes > options.CoverBudgetBytes)
            throw new ImageStorageFullException("Image storage budget reached.");
        CheckFreeSpace(prepared.Bytes);
        Directory.CreateDirectory(destination);
        CheckExistingDirectories(destination);
        var temporary = Path.Combine(destination, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var source = File.OpenRead(prepared.SelectedPath))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            await MarkDirtyAsync(cancellationToken);
            File.Move(temporary, target, true);
            await WriteInventoryAsync(new Inventory(1, usage.Bytes - oldSize + prepared.Bytes), CancellationToken.None);
        }
        finally
        {
            File.Delete(temporary);
        }
        return Describe(prepared, target);
    }

    private PublishedImage Describe(PreparedImage prepared, string target)
        => new(prepared.Hash, prepared.ContentType, prepared.Bytes,
            Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/'), target);

    private void CheckStagedImage(string path, string workingDirectory)
    {
        var work = Path.GetFullPath(workingDirectory);
        if (!Path.GetDirectoryName(work)!.Equals(stagingRoot, pathComparison)
            || !Guid.TryParseExact(Path.GetFileName(work), "N", out _)
            || !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(work, pathComparison))
            throw new InvalidDataException("Image must belong to its private staging directory.");
        CheckExistingDirectories(work);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Staged image cannot be a symbolic link.");
    }

    private async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(Path.Combine(root, ".storage.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private async Task<Inventory> ReadInventoryAsync(CancellationToken cancellationToken)
    {
        var dirty = Path.Combine(root, ".storage-dirty");
        if (reconciled && !File.Exists(dirty))
        {
            try
            {
                await using var stream = File.OpenRead(Path.Combine(root, ".storage-inventory.json"));
                var stored = await JsonSerializer.DeserializeAsync<Inventory>(stream, cancellationToken: cancellationToken);
                if (stored is { Version: 1, Bytes: >= 0 })
                    return stored;
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                // Reconcile an interrupted or externally removed inventory before the next write.
            }
        }

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        var bytes = Directory.EnumerateFiles(root, "*", enumeration)
            .Where(path => IsHash(Path.GetFileName(path)))
            .Sum(path => new FileInfo(path).Length);
        var inventory = new Inventory(1, bytes);
        await WriteInventoryAsync(inventory, cancellationToken);
        reconciled = true;
        return inventory;
    }

    private async Task MarkDirtyAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(Path.Combine(root, ".storage-dirty"), FileMode.Create,
            FileAccess.Write, FileShare.None);
        await stream.WriteAsync(new byte[] { 1 }, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
    }

    private async Task WriteInventoryAsync(Inventory inventory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(root, ".storage-inventory.json");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, inventory, cancellationToken: cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(true);
            }
            File.Move(temporary, target, true);
            File.Delete(Path.Combine(root, ".storage-dirty"));
        }
        catch
        {
            reconciled = false;
            throw;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private void EnsureWithinRoot(string path)
    {
        if (!path.Equals(root, pathComparison)
            && !path.StartsWith(root + Path.DirectorySeparatorChar, pathComparison))
            throw new InvalidDataException("Image destination must stay inside the configured media root.");
    }

    private void CheckExistingDirectories(string path)
    {
        EnsureWithinRoot(path);
        for (var current = path; !current.Equals(root, pathComparison); current = Path.GetDirectoryName(current)!)
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Image directories cannot contain symbolic links.");
    }

    private static bool IsHash(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
