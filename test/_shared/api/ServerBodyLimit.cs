using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// A request body budget enforced the way a <b>web server</b> enforces one — the seam that makes
/// <see cref="BadHttpRequestException"/> reachable from a fact.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a substitution rather than the real thing.</b> Kestrel's <c>MaxRequestBodySize</c> is the
/// production source of a 413, and <c>TestServer</c> implements no request-body size limit at all — it does
/// not even carry <c>IHttpMaxRequestBodySizeFeature</c> — so over the fixture every suite in this repository
/// uses, the exception the exception handler has to classify simply cannot occur. That is the same shape of
/// problem <see cref="FaultingAlvoData"/> solves for <c>IAlvoData</c>'s fifth family, and it takes the same
/// answer: substitute the one thing that cannot happen, and keep everything around it real.
/// </para>
/// <para>
/// <b>What stays real is what the fact is about.</b> The exception is
/// <see cref="BadHttpRequestException"/> with <c>StatusCode = 413</c> — the type and the status Kestrel
/// itself raises — and it is thrown from a <c>Read</c> on <c>HttpRequest.Body</c> <em>inside</em> the
/// endpoint, which is where Kestrel raises it too. So the endpoint, the middleware pipeline, the handler's
/// ownership test and the response are all the production ones; only the byte counter is this file's.
/// </para>
/// <para>
/// The budget is deliberately set <em>below</em> <c>AlvoApiOptions.MaxRequestBodyBytes</c> by the facts that
/// use it, which is also the only deployment where the server wins the race: Alvo refuses an over-declared
/// <c>Content-Length</c> before reading a byte, so with the default 1 MB bound a caller reaches Alvo's own
/// 422 first, and a server limit is felt only when an operator has set one lower.
/// </para>
/// </remarks>
internal static class ServerBodyLimit
{
    /// <summary>Middleware that puts every request body under <paramref name="maxBytes"/>.</summary>
    /// <param name="maxBytes">The budget, in bytes.</param>
    internal static Func<HttpContext, RequestDelegate, Task> Enforcing(int maxBytes) =>
        async (context, next) =>
        {
            context.Request.Body = new BoundedStream(context.Request.Body, maxBytes);
            await next(context);
        };

    /// <summary>A read-only stream that refuses once the budget is crossed, exactly as the server would.</summary>
    private sealed class BoundedStream(Stream inner, int maxBytes) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Counted(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Counted(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>
        /// Counts what a read produced and refuses the request the moment the budget is crossed.
        /// </summary>
        /// <remarks>
        /// The refusal is on what has actually <em>arrived</em>, not on a declared length: that is how a
        /// server bounds a chunked body, and a check on <c>Content-Length</c> alone would be a bound a
        /// caller chooses.
        /// </remarks>
        /// <param name="read">The bytes this read produced.</param>
        private int Counted(int read)
        {
            _read += read;

            return _read > maxBytes
                ? throw new BadHttpRequestException(
                    $"Request body too large. The max request body size is {maxBytes} bytes.",
                    StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
