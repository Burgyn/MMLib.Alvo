using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Ai.Internal;

/// <summary>How many UTF-8 bytes a patch adds to a document: one measure for its values and for what it copies.</summary>
/// <remarks>
/// A value is measured as written in the patch; a copied subtree as it would serialize, counted into a reused scratch
/// buffer so measuring a large subtree never allocates its size. Neither text is emitted anywhere, which is why the
/// counting writer may skip HTML-safe escaping: escaping would overstate a non-ASCII character six-fold.
/// </remarks>
internal static class PatchBytes
{
    private const int NullLiteralBytes = 4;

    private static readonly JsonWriterOptions _countingOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true,
    };

    /// <summary>The bytes of <paramref name="operation"/>'s <c>value</c> member, or 0 when it has none.</summary>
    internal static long OfValue(JsonElement operation) =>
        operation.ValueKind == JsonValueKind.Object && operation.TryGetProperty("value", out var value)
            ? Encoding.UTF8.GetByteCount(value.GetRawText())
            : 0;

    /// <summary>The bytes <paramref name="node"/> serializes to.</summary>
    internal static long Of(JsonNode? node)
    {
        if (node is null)
        {
            return NullLiteralBytes;
        }

        var counter = new CountingBufferWriter();
        using (var writer = new Utf8JsonWriter(counter, _countingOptions))
        {
            node.WriteTo(writer);
        }

        return counter.Count;
    }

    private sealed class CountingBufferWriter : IBufferWriter<byte>
    {
        private const int InitialScratchBytes = 256;

        private byte[] _scratch = new byte[InitialScratchBytes];

        internal long Count { get; private set; }

        public void Advance(int count) => Count += count;

        public Memory<byte> GetMemory(int sizeHint = 0) => Scratch(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Scratch(sizeHint).Span;

        private Memory<byte> Scratch(int sizeHint)
        {
            if (sizeHint > _scratch.Length)
            {
                _scratch = new byte[sizeHint];
            }

            return _scratch;
        }
    }
}
