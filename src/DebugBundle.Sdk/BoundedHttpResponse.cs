using System.Text;

namespace DebugBundle;

internal static class BoundedHttpResponse
{
    internal const int MaxBytes = 256 * 1024;
    // Native streaming reads use the same cancellation deadline as connection
    // establishment. Custom handlers/streams must honour cancellation themselves.
    internal static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentEncoding.Any(encoding => !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("unsupported native reply encoding");
        if (response.Content.Headers.ContentLength > MaxBytes) throw new IOException("native reply exceeds byte limit");
#if NET8_0_OR_GREATER
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaxBytes + 1 - (int)output.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            output.Write(buffer, 0, read);
            if (output.Length > MaxBytes) throw new IOException("native reply exceeds byte limit");
        }
        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }
}
