using Microsoft.AspNetCore.Http;
using System.Text.Json;

namespace MMLib.Alvo.Api.Internal;

/// <summary>Why a request body was refused before anything could be read out of it.</summary>
/// <remarks>
/// An enum rather than a violation, because the <em>mechanics</em> of bounding a body are shared by the
/// write path and the query path while the <em>wording</em> is not: a read endpoint that answered "a write
/// payload is a flat map of the entity's declared fields" would hand an agent a fix for another operation.
/// Each surface's own catalogue maps this to its own violation, under the code
/// <see cref="BoundedJsonBody.CodeOf"/> gives — so the two can differ in prose and cannot differ in code.
/// </remarks>
internal enum BodyRefusal
{
    /// <summary>The body is not a JSON object.</summary>
    NotAnObject,

    /// <summary>The body is not well-formed JSON.</summary>
    MalformedJson,

    /// <summary>The body is past the configured byte bound.</summary>
    TooLarge,

    /// <summary>The body nests past the configured depth bound.</summary>
    TooDeep,

    /// <summary>The body carries more property names, at any depth, than the bound allows.</summary>
    TooManyKeys,

    /// <summary>One object in the body uses the same property name twice.</summary>
    DuplicateName,
}

/// <summary>
/// Reads a request body under Alvo's three payload bounds and decides its shape, without binding anything
/// to an entity and without composing a message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every bound refuses <em>before</em> the work it exists to prevent.</b> The size bound stops at the
/// first chunk that would cross it rather than buffering the body first, and the depth and key bounds are
/// decided by a forward-only <see cref="Utf8JsonReader"/> scan that builds no node tree. A bound applied to
/// a finished document has already paid the cost it exists to prevent.
/// </para>
/// <para>
/// <b>It hands back the buffer, not a parsed document.</b> The two callers parse differently on purpose:
/// <see cref="JsonPayloadReader"/> needs a <c>JsonNode</c> tree to bind field values and to digest for an
/// idempotency fingerprint, while <c>QueryBodyReader</c> needs a <c>JsonDocument</c> so a number can
/// contribute the literal text the caller wrote. Parsing here would force one of them to convert.
/// </para>
/// <para>
/// <b>The scan is exactly one level stricter than either parse, and that was measured rather than
/// reasoned.</b> <see cref="JsonDocumentOptions.MaxDepth"/> counts the outermost container as level 1 where
/// <see cref="Utf8JsonReader.CurrentDepth"/> reports it as 0, so the two numbers look like an off-by-one
/// waiting to turn an accepted body into an uncaught <see cref="JsonException"/>. At
/// <see cref="AlvoApiOptions.MaxPayloadDepth"/> levels the scan accepts and the parse succeeds; one level
/// deeper the scan refuses first. <c>PayloadBindingTests</c> and <c>QueryBodyReaderTests</c> hold the
/// boundary on both readers.
/// </para>
/// </remarks>
internal static class BoundedJsonBody
{
    /// <summary>One buffer's worth of body; the size bound trips on chunk boundaries, so this only sets the granularity.</summary>
    private const int ReadChunkBytes = 8 * 1024;

    /// <summary>
    /// Copies the request body into <paramref name="destination"/> and decides its shape, or reports the one
    /// bound that stopped it.
    /// </summary>
    /// <param name="request">The request whose body to read.</param>
    /// <param name="destination">Where the body is buffered; the caller owns it.</param>
    /// <param name="options">The payload bounds to enforce.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    internal static async Task<BodyRefusal?> ReadAsync(
        HttpRequest request,
        MemoryStream destination,
        AlvoApiOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);

        var readFailure = await ReadBoundedAsync(
            request, destination, options.MaxRequestBodyBytes, cancellationToken).ConfigureAwait(false);

        return readFailure
            ?? EnsureWithinShapeBounds(destination.GetBuffer().AsSpan(0, (int)destination.Length), options);
    }

    /// <summary>The stable code a refusal is published under, whichever surface words it.</summary>
    /// <param name="refusal">The bound that stopped the body.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="refusal"/> is not one of the named cases.</exception>
    internal static string CodeOf(BodyRefusal refusal) => refusal switch
    {
        BodyRefusal.NotAnObject => "not-an-object",
        BodyRefusal.MalformedJson => "malformed-json",
        BodyRefusal.TooLarge => "body-too-large",
        BodyRefusal.TooDeep => "body-too-deep",
        BodyRefusal.TooManyKeys => "body-too-many-fields",
        BodyRefusal.DuplicateName => "duplicate-field",
        _ => throw new ArgumentOutOfRangeException(
            nameof(refusal), refusal, "Unmapped body refusal; give it a stable code here."),
    };

    /// <summary>
    /// Copies the body into <paramref name="destination"/>, refusing at the first chunk that would cross
    /// <paramref name="maxBytes"/>. A declared <c>Content-Length</c> past the bound is refused without
    /// reading a byte; a chunked body that declares no length is bounded all the same, because the check
    /// is on what has actually arrived.
    /// </summary>
    private static async Task<BodyRefusal?> ReadBoundedAsync(
        HttpRequest request, MemoryStream destination, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return BodyRefusal.TooLarge;
        }

        var chunk = new byte[ReadChunkBytes];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > maxBytes)
            {
                return BodyRefusal.TooLarge;
            }

            destination.Write(chunk, 0, read);
        }

        return null;
    }

    /// <summary>
    /// Decides the shape bounds — is it an object at all, how deep does it nest, how many property names does
    /// it carry <em>anywhere</em> — from a forward-only scan that builds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reader's own <see cref="JsonReaderOptions.MaxDepth"/> is deliberately given headroom over
    /// <see cref="AlvoApiOptions.MaxPayloadDepth"/>. The reader raises the same <see cref="JsonException"/>
    /// for a too-deep body as for a malformed one, so anything the reader refuses could only ever be reported
    /// as "not well-formed JSON" — the one bound whose message could not name itself, which sends an agent
    /// hunting a syntax error that is not there. Checking <see cref="Utf8JsonReader.CurrentDepth"/> first
    /// means the depth refusal names the depth.
    /// </para>
    /// <para>
    /// The headroom is <b>two</b> levels, not one, because the two numbers are counted differently:
    /// <see cref="JsonReaderOptions.MaxDepth"/> counts the outermost container as level 1 while
    /// <see cref="Utf8JsonReader.CurrentDepth"/> reports it as 0. With only one level of slack the reader
    /// threw on the very token whose <see cref="Utf8JsonReader.CurrentDepth"/> the check needed to see, and
    /// the named message was unreachable — measured, not reasoned. The reader remains a hard backstop; it is
    /// simply never the first to speak.
    /// </para>
    /// </remarks>
    private static BodyRefusal? EnsureWithinShapeBounds(ReadOnlySpan<byte> utf8Body, AlvoApiOptions options)
    {
        var reader = new Utf8JsonReader(
            utf8Body,
            new JsonReaderOptions { MaxDepth = options.MaxPayloadDepth + 2, AllowTrailingCommas = false });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return BodyRefusal.NotAnObject;
            }

            return ScanShape(ref reader, options);
        }
        catch (JsonException)
        {
            return BodyRefusal.MalformedJson;
        }
    }

    /// <summary>
    /// Walks every token of the body, refusing as soon as the property-name count or the nesting depth
    /// crosses its bound — or as soon as one object uses a name twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Property names are counted at every depth, not just the top level.</b> Counting only depth 1 was a
    /// bound that did not bound: <c>{"name":{…150 000 keys…}}</c> satisfied it, satisfied the depth cap at
    /// depth 2, fitted inside <see cref="AlvoApiOptions.MaxRequestBodyBytes"/>, and was then materialised in
    /// full — a ~20–40× memory amplification per request, refused only afterwards. The scan already visits
    /// every token, so this is a counter placement rather than a second pass.
    /// </para>
    /// <para>
    /// <b>The duplicate-name check is here for the same reason, and it is the one bound whose absence was a
    /// leak rather than a cost.</b> A repeated name passed this scan and passed <c>JsonNode.Parse</c> too,
    /// because a <c>JsonObject</c>'s backing dictionary materialises lazily — so the binder was the first
    /// thing to touch it and threw <see cref="ArgumentException"/> with a .NET dictionary message that ended
    /// in the caller's own key. Deciding it here refuses the body <em>before</em> the node tree exists, which
    /// is this type's rule for every other bound.
    /// </para>
    /// <para>
    /// The comparison matches <c>JsonObject</c>'s own exactly, which is what makes this a pre-emption rather
    /// than a second opinion: the name is read through <see cref="Utf8JsonReader.GetString"/>, and it is
    /// <see cref="StringComparer.Ordinal"/>, so <c>a</c> and <c>A</c> are two names.
    /// </para>
    /// </remarks>
    private static BodyRefusal? ScanShape(ref Utf8JsonReader reader, AlvoApiOptions options)
    {
        var names = 0;
        var seen = new NamesByDepth();

        seen.Enter(reader.CurrentDepth + 1);

        while (reader.Read())
        {
            if (reader.CurrentDepth > options.MaxPayloadDepth)
            {
                return BodyRefusal.TooDeep;
            }

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                seen.Enter(reader.CurrentDepth + 1);
            }
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (++names > options.MaxPayloadKeys)
                {
                    return BodyRefusal.TooManyKeys;
                }

                if (!seen.Add(reader.CurrentDepth, reader.GetString()!))
                {
                    return BodyRefusal.DuplicateName;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The property names seen so far in the object currently open at each depth — enough to decide "this
    /// object already has that name" from a forward-only scan that keeps no node tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keyed by depth and cleared on entry, which is what makes sibling objects independent.</b> In
    /// <c>{"a":{"x":1},"b":{"x":2}}</c> both <c>x</c> sit at the same depth and are <em>not</em> duplicates, so
    /// a single set for the whole body would refuse a perfectly ordinary payload. Clearing the depth's set
    /// every time an object opens is what scopes it to one object rather than to one level.
    /// </para>
    /// <para>
    /// The sets are reused rather than allocated per object, because a wide array of small objects would
    /// otherwise allocate one <see cref="HashSet{T}"/> per element on a path that already refuses to build a
    /// node tree. Both the number of sets and their total contents are bounded by
    /// <see cref="AlvoApiOptions.MaxPayloadDepth"/> and <see cref="AlvoApiOptions.MaxPayloadKeys"/>, which the
    /// same loop enforces.
    /// </para>
    /// </remarks>
    private sealed class NamesByDepth
    {
        private readonly List<HashSet<string>> _byDepth = [];

        /// <summary>Opens a fresh object whose own property names will be reported at <paramref name="depth"/>.</summary>
        /// <param name="depth">The depth this object's property names are reported at.</param>
        internal void Enter(int depth)
        {
            while (_byDepth.Count <= depth)
            {
                _byDepth.Add(new HashSet<string>(StringComparer.Ordinal));
            }

            _byDepth[depth].Clear();
        }

        /// <summary>
        /// Records one property name, answering <see langword="false"/> when the object open at
        /// <paramref name="depth"/> already carried it.
        /// </summary>
        /// <remarks>
        /// The depth is always one <see cref="Enter"/> has seen: a property name is only ever reported inside an
        /// object, and every object's opening brace — the root's included — enters before its names arrive.
        /// </remarks>
        /// <param name="depth">The depth the name was reported at.</param>
        /// <param name="name">The property name.</param>
        internal bool Add(int depth, string name) => _byDepth[depth].Add(name);
    }
}
