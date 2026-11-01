using System.Net;
using System.Text;
using DebugBundle;
using DebugBundle.Transport;

namespace DebugBundle.Sdk.Tests;

public sealed class BoundedHttpResponseTests
{
    private sealed class UnknownLengthContent(byte[] data) : ByteArrayContent(data)
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    [Theory]
    [InlineData("valid", true)]
    [InlineData("encoding", false)]
    [InlineData("advertised", false)]
    [InlineData("streaming", false)]
    [InlineData("utf8", false)]
    public async Task NativeReplyReaderBoundsDecodedBytesAndRequiresIdentityUtf8(string mode, bool valid)
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Accepted);
        response.Content = mode == "utf8" ? new ByteArrayContent(new byte[] { 0xff }) :
            new ByteArrayContent(Encoding.UTF8.GetBytes(mode is "advertised" or "streaming" ? new string('x', 262145) : "{}"));
        if (mode == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
        if (mode == "streaming") response.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(new string('x', 262145)));
        if (valid) Assert.Equal("{}", await BoundedHttpResponse.ReadAsync(response, CancellationToken.None));
        else await Assert.ThrowsAnyAsync<Exception>(() => BoundedHttpResponse.ReadAsync(response, CancellationToken.None));
    }
    private sealed class DelayedStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); return 0; }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class StreamingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StreamContent(new DelayedStream()) });
    }
    [Fact]
    public async Task NativeHttpDeadlineContinuesAfterHeadersIntoStreamingBody()
    {
        using var handler = new StreamingHandler(); using var http = new HttpClient(handler);
        using var transport = new HttpEventTransport(new Uri("https://example.invalid/events"), TimeSpan.FromMilliseconds(40), http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.SendAsync(new EventTransportRequest()));
    }
}
