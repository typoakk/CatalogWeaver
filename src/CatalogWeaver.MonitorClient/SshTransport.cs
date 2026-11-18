using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CatalogWeaver.MonitorClient;

public sealed class SshTransport : IDisposable
{
    private static readonly HttpRequestOptionsKey<SshTarget> Target = new("CatalogWeaver.SshTarget");

    internal HttpClient Client { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 4,
        ConnectCallback = OpenAsync
    }) { Timeout = Timeout.InfiniteTimeSpan };

    internal static HttpRequestMessage Request(SshTarget target, string path)
    {
        // Separate the HTTP connection pools by SSH host and API port.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(target.Host));
        var poolHost = Convert.ToHexStringLower(digest.AsSpan(0, 16)) + ".invalid";
        var origin = new UriBuilder("http", poolHost, target.ApiPort).Uri;
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, path));
        request.Headers.Host = $"127.0.0.1:{target.ApiPort}";
        request.Options.Set(Target, target);
        return request;
    }

    private static ValueTask<Stream> OpenAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!context.InitialRequestMessage.Options.TryGetValue(Target, out var target))
            throw new InvalidOperationException("Missing SSH target.");

        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe") : "ssh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in new[]
        {
            "-T", "-W", $"127.0.0.1:{target.ApiPort}",
            "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
            "-o", "ConnectTimeout=8", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2",
            "-o", "ClearAllForwardings=yes", "-o", "ExitOnForwardFailure=yes",
            "-o", "StdinNull=no", "-o", "ForkAfterAuthentication=no", "-o", "PermitLocalCommand=no",
            "-S", "none", "--", target.Host
        })
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new ClientConnectionException("monitor_ssh_unavailable");
        }
        catch (Win32Exception)
        {
            throw new ClientConnectionException("monitor_ssh_unavailable");
        }

        // Drain SSH diagnostics without exposing local paths or configuration to the browser or logs.
        process.BeginErrorReadLine();
        return ValueTask.FromResult<Stream>(new ForwardedStream(process));
    }

    public void Dispose() => Client.Dispose();

    private sealed class ForwardedStream(Process process) : Stream
    {
        private readonly Stream input = process.StandardInput.BaseStream;
        private readonly Stream output = process.StandardOutput.BaseStream;
        private int disposed;

        public override bool CanRead => output.CanRead;
        public override bool CanWrite => input.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => output.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => output.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => input.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => input.WriteAsync(buffer, ct);
        public override void Flush() => input.Flush();
        public override Task FlushAsync(CancellationToken ct) => input.FlushAsync(ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref disposed, 1) == 0)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                finally
                {
                    input.Dispose();
                    output.Dispose();
                    process.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
