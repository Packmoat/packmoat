using System.Text;

namespace Packmoat.Application;

/// <summary>Raised when a response is bigger than we are willing to hold in memory.</summary>
public sealed class ResponseTooLargeException : IOException
{
    public ResponseTooLargeException(long limit) : base($"Response is larger than the {limit}-byte limit.") { }
}

/// <summary>
/// Reads text from a stream but stops as soon as the limit is passed. A Content-Length check is not enough:
/// chunked responses have none, and a gzip bomb is tiny on the wire and huge after decompression.
/// </summary>
public static class BoundedText
{
    public static async Task<string> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new ResponseTooLargeException(maxBytes);
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
