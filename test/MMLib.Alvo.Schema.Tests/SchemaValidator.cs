using Corvus.Json;
using Corvus.Json.Validator;
using System.Text.Json;

namespace MMLib.Alvo.Schema.Tests;

internal static class SchemaValidator
{
    /// <summary>
    /// The descriptor schema, built <b>once per process</b> and shared by every fact that validates against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Building it is the whole cost of this suite, and building it four times at once is worse than four
    /// times the cost.</b> Measured: <c>JsonSchema.FromFile</c> takes <b>~6.5 s</b> on a cold call and <b>0 ms</b>
    /// once Corvus has it cached; one <c>Validate</c> at <see cref="ValidationLevel.Detailed"/> costs ~1.5 ms,
    /// so validation is not where the time goes. Four test classes each called <c>Load()</c> and xUnit ran them
    /// as parallel collections, so all four missed the cache simultaneously and built the same schema
    /// concurrently — and under that contention each took <b>~23 s</b> rather than 6.5 s, which was two thirds
    /// of ring0.
    /// </para>
    /// <para>
    /// <see cref="Lazy{T}"/> with <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> is the fix: the
    /// first caller builds, the other three wait on that one build instead of racing it. The call sites are
    /// unchanged, so this cannot drift from what they ask for.
    /// </para>
    /// </remarks>
    private static readonly Lazy<JsonSchema> _schema = new(
        () => JsonSchema.FromFile(SchemaPaths.SchemaFile), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static JsonSchema Load() => _schema.Value;

    internal static IReadOnlyList<(string Pointer, string Message)> Failures(JsonSchema schema, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ValidationContext context = schema.Validate(document.RootElement, ValidationLevel.Detailed);
        if (context.IsValid)
        {
            return [];
        }

        return context.Results
            .Where(result => !result.Valid)
            .Select(result => (
                result.Location?.DocumentLocation.ToString() ?? string.Empty,
                result.Message ?? string.Empty))
            .ToList();
    }
}
