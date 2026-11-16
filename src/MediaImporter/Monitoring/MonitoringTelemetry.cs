using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace MediaImporter.Monitoring;

public sealed record HttpMeasurement(Guid Id, DateTime Utc, string Provider, string Category, string Host,
    int? Status, string? Error, long Received, long Sent, double DurationMs);

public sealed class MonitoringTelemetry
{
    private readonly ConcurrentQueue<HttpMeasurement> completed = new();
    private readonly ConcurrentDictionary<Guid, Request> active = new();
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public object[] Active => active.Values.Select(x => x.View()).ToArray();
    public Request Start(string provider, HttpRequestMessage message, string? category = null)
    {
        var request = new Request(this, provider, message, category);
        active[request.Id] = request;
        return request;
    }
    public List<HttpMeasurement> Drain()
    {
        var result = new List<HttpMeasurement>();
        while (result.Count < 2000 && completed.TryDequeue(out var entry)) result.Add(entry);
        return result;
    }
    public sealed class Request : IDisposable
    {
        private readonly MonitoringTelemetry owner;
        private readonly string provider, host, category;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly DateTime utc = DateTime.UtcNow;
        private long received, sent;
        private long? expected;
        private int? status;
        private string? error;
        private bool disposed;
        public Guid Id { get; } = Guid.NewGuid();
        internal Request(MonitoringTelemetry owner, string provider, HttpRequestMessage message, string? category)
        {
            this.owner = owner; this.provider = provider; host = message.RequestUri!.IdnHost;
            this.category = category ?? (message.Headers.Accept.Any(x => x.MediaType?.StartsWith("image/", StringComparison.Ordinal) == true) ? "images" : "metadata");
            if (message.Content is { } content) message.Content = new MeasuredContent(content, this).Initialize();
        }
        public void Headers(HttpResponseMessage response) { status = (int)response.StatusCode; expected = response.Content.Headers.ContentLength; }
        public void Read(int count) => Interlocked.Add(ref received, count);
        public void Fail(Exception failure) => error = failure is AdapterReadException known ? known.Code
            : failure is OperationCanceledException ? "cancelled" : "transport_error";
        public object View() => new { id = Id, provider, host, category, startedUtc = utc, expected, status,
            received = Interlocked.Read(ref received), sent = Interlocked.Read(ref sent), durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            owner.active.TryRemove(Id, out _);
            if (owner.completed.Count >= 10000) { Interlocked.Increment(ref owner.dropped); return; }
            owner.completed.Enqueue(new(Id, utc, provider, category, host, status, error,
                Interlocked.Read(ref received), Interlocked.Read(ref sent), Stopwatch.GetElapsedTime(started).TotalMilliseconds));
        }
        private sealed class MeasuredContent(HttpContent inner, Request trace) : HttpContent
        {
            public MeasuredContent Initialize() { foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value); return this; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(new CountingStream(stream, trace));
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) => inner.CopyToAsync(new CountingStream(stream, trace), ct);
            protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
            protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        }
        private sealed class CountingStream(Stream inner, Request trace) : Stream
        {
            public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
            public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); Interlocked.Add(ref trace.sent, count); }
            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { await inner.WriteAsync(buffer.AsMemory(offset, count), ct); Interlocked.Add(ref trace.sent, count); }
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { await inner.WriteAsync(buffer, ct); Interlocked.Add(ref trace.sent, buffer.Length); }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
        }
    }
}
