using Microsoft.AspNetCore.Http.Features;

// Counts HTTP body bytes as streams are read/written, including chunked uploads and
// range/file responses. No buffering, credentials, payloads or per-client identifiers.
static class TrafficMeter
{
    public static async Task Measure(HttpContext context, Func<Task> next, ServerMetrics metrics)
    {
        var request = context.Request.Body;
        var response = context.Features.Get<IHttpResponseBodyFeature>()!;
        context.Request.Body = new MeteredStream(request, metrics.Receive, _ => { });
        var countedResponse = new StreamResponseBodyFeature(
            new MeteredStream(response.Stream, _ => { }, metrics.Send), response);
        context.Features.Set<IHttpResponseBodyFeature>(countedResponse);
        try
        {
            await next();
            await countedResponse.CompleteAsync();
        }
        finally
        {
            context.Request.Body = request;
            context.Features.Set(response);
        }
    }

    private sealed class MeteredStream(Stream inner, Action<int> received, Action<int> sent) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override int Read(byte[] buffer, int offset, int count)
        { var size = inner.Read(buffer, offset, count); received(size); return size; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { var size = await inner.ReadAsync(buffer.AsMemory(offset, count), token); received(size); return size; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { var size = await inner.ReadAsync(buffer, token); received(size); return size; }
        public override void Write(byte[] buffer, int offset, int count)
        { inner.Write(buffer, offset, count); sent(count); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { await inner.WriteAsync(buffer.AsMemory(offset, count), token); sent(count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { await inner.WriteAsync(buffer, token); sent(buffer.Length); }
    }
}
