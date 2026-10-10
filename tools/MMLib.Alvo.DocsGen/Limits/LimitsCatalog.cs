using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.DocsGen.Xml;
using System.Globalization;
using System.Reflection;

namespace MMLib.Alvo.DocsGen.Limits;

internal sealed record Limit(string Name, string Value, string? ConfigurationKey, string Description, string Source);

internal static class LimitsCatalog
{
    private const string ApiSection = "Alvo:Api";
    private const int BytesPerMebibyte = 1024 * 1024;
    private const string AuthValidator = "MMLib.Alvo.Auth.Internal.AlvoAuthOptionsValidator";
    private const string CelParser = "MMLib.Alvo.Expressions.Internal.CelParser";

    internal static IReadOnlyList<Limit> Read(XmlDocs docs)
    {
        var api = new AlvoApiOptions();
        var core = typeof(AlvoApiOptions).Assembly;
        return
        [
            ApiOption(docs, "Default page size", nameof(AlvoApiOptions.DefaultPageSize), Number(api.DefaultPageSize)),
            ApiOption(docs, "Maximum page size", nameof(AlvoApiOptions.MaxPageSize), Number(api.MaxPageSize)),
            ApiOption(docs, "Request body", nameof(AlvoApiOptions.MaxRequestBodyBytes), Bytes(api.MaxRequestBodyBytes)),
            ApiOption(docs, "Payload depth", nameof(AlvoApiOptions.MaxPayloadDepth), Number(api.MaxPayloadDepth)),
            ApiOption(docs, "Payload keys", nameof(AlvoApiOptions.MaxPayloadKeys), Number(api.MaxPayloadKeys)),
            ApiOption(docs, "Batch rows", nameof(AlvoApiOptions.MaxBatchRows), Number(api.MaxBatchRows)),
            ApiOption(docs, "`Idempotency-Key` length", nameof(AlvoApiOptions.MaxIdempotencyKeyBytes), Number(api.MaxIdempotencyKeyBytes) + " bytes"),
            Member(docs, "Filter depth", typeof(AlvoFilter).GetProperty(nameof(AlvoFilter.MaxDepth))!),
            Member(docs, "Filter terms", typeof(AlvoFilter).GetProperty(nameof(AlvoFilter.MaxTerms))!),
            Member(docs, "`in` candidates", typeof(AlvoFilter).GetProperty(nameof(AlvoFilter.MaxInCandidates))!),
            Member(docs, "Dev-key secret minimum", ConstantOf(core, AuthValidator, "MinimumSecretLength"), " characters"),
            Member(docs, "CEL nesting depth", ConstantOf(core, CelParser, "MaxDepth")),
        ];
    }

    private static Limit ApiOption(XmlDocs docs, string name, string property, string value)
    {
        var member = typeof(AlvoApiOptions).GetProperty(property)!;
        var source = $"{nameof(AlvoApiOptions)}.{property}";
        return new Limit(name, value, $"{ApiSection}:{property}", Describe(docs, DocId.Of(member), source), source);
    }

    private static Limit Member(XmlDocs docs, string name, PropertyInfo property)
    {
        var source = $"{property.DeclaringType!.Name}.{property.Name}";
        return new(name, Number(property.GetValue(null)!), null, Describe(docs, DocId.Of(property), source), source);
    }

    private static Limit Member(XmlDocs docs, string name, FieldInfo field, string unit = "")
    {
        var source = $"{field.DeclaringType!.Name}.{field.Name}";
        return new(name, Number(field.GetRawConstantValue()!) + unit, null, Describe(docs, DocId.Of(field), source), source);
    }

    private static FieldInfo ConstantOf(Assembly assembly, string typeName, string fieldName) =>
        assembly.GetType(typeName, throwOnError: true)!.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"{typeName}.{fieldName} no longer exists; the limits page reads it.");

    private static string Describe(XmlDocs docs, string docId, string source) =>
        LimitTexts.BySource.TryGetValue(source, out var reader) ? reader
        : docs.Summary(docId) is { Length: > 0 } summary ? summary
        : throw new InvalidOperationException($"{docId} has neither a reader-facing text in LimitTexts nor an XML <summary>.");

    private static string Number(object value) => Convert.ToString(value, CultureInfo.InvariantCulture)!;

    private static string Bytes(int bytes) =>
        bytes % BytesPerMebibyte == 0
            ? $"{Number(bytes)} bytes ({Number(bytes / BytesPerMebibyte)} MiB)"
            : $"{Number(bytes)} bytes";
}
