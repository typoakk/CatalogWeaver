using System.Diagnostics;
using System.ComponentModel;
using System.Text;

namespace MediaImporter.Images;

public sealed class ProcessDeadlineException(int processId) : IOException("Image operation exceeded its execution deadline.")
{
    public int ProcessId { get; } = processId;
}

/// <summary>Cancellation returns only after the child and its descendants have been terminated.</summary>
public static class BoundedProcess
{
    public static async Task<string> RunAsync(ProcessStartInfo start, string input, TimeSpan timeout, CancellationToken ct)
    {
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Could not start the image worker.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, budget.Token);
        var errors = ReadBoundedAsync(process.StandardError, budget.Token);
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), budget.Token);
            process.StandardInput.Close();
            var exited = process.WaitForExitAsync(budget.Token);
            var pending = new List<Task> { exited, output, errors };
            while (!exited.IsCompleted)
            {
                var completed = await Task.WhenAny(pending);
                await completed; // A bounded reader failure must terminate the child immediately.
                pending.Remove(completed);
            }
            await exited;
            var result = await output;
            await errors;
            if (process.ExitCode != 0) throw new InvalidDataException("Image worker could not process the image.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ProcessDeadlineException(process.Id); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            budget.Cancel();
            try { await Task.WhenAll(output, errors); } catch (Exception e) when (e is OperationCanceledException or IOException or InvalidDataException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return output.ToString();
            if (output.Length + count > 65536) throw new InvalidDataException("Image worker response exceeded its limit.");
            output.Append(buffer, 0, count);
        }
    }
}
