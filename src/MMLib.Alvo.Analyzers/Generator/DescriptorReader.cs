using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>
/// A descriptor's text — or a Management API snapshot carrying one in <c>descriptorJson</c> — as a <see cref="DescriptorModel"/>,
/// the managed columns added exactly as the applied schema adds them; or the reason it cannot be read.
/// </summary>
/// <remarks>Tolerant: every key the model does not read is ignored, so a later descriptor feature never breaks generation.</remarks>
internal static class DescriptorReader
{
    internal const string ApiVersion = "alvo.dev/v1";

    private static readonly ImmutableArray<string> _builtInRoles = ImmutableArray.Create("anon", "authenticated", "admin");

    /// <summary>Reads <paramref name="text"/>; exactly one of <see cref="DescriptorReadResult.Model"/> and <see cref="DescriptorReadResult.Error"/> is set.</summary>
    /// <param name="file">The file's path; the model keeps its file name.</param>
    /// <param name="text">The descriptor or snapshot text.</param>
    /// <param name="namespace">The namespace the generated types go in.</param>
    internal static DescriptorReadResult Read(string file, string text, string @namespace)
    {
        var name = Path.GetFileName(file);
        try
        {
            var root = Unwrap(Parse(text, "The JSON", positioned: true));

            return new(name, Model(name, Checked(root), @namespace), null, 0, 0);
        }
        catch (DescriptorRefusal refusal)
        {
            return new(name, null, refusal.Message, refusal.Line, refusal.Column);
        }
    }

    private static JsonNode Parse(string text, string what, bool positioned)
    {
        try
        {
            return DescriptorJson.Parse(text);
        }
        catch (DescriptorJsonException malformed)
        {
            throw new DescriptorRefusal(
                $"{what} is malformed at line {malformed.Line}, column {malformed.Column}: {malformed.Message}.",
                positioned ? malformed.Line : 0,
                positioned ? malformed.Column : 0);
        }
    }

    private static JsonNode Unwrap(JsonNode root) =>
        root is JsonObject snapshot && snapshot.Get("descriptorJson") is JsonString carried
            ? Parse(carried.Value, "The snapshot's descriptorJson (the position is inside that string)", positioned: false)
            : root;

    private static JsonObject Checked(JsonNode root)
    {
        if (root is not JsonObject descriptor)
        {
            throw new DescriptorRefusal("The root is not a JSON object.");
        }

        if (descriptor.String("apiVersion") is var version && !string.Equals(version, ApiVersion, StringComparison.Ordinal))
        {
            throw new DescriptorRefusal(
                $"apiVersion is {(version is null ? "missing" : $"'{version}'")}, and the generator reads '{ApiVersion}' only.");
        }

        return descriptor.Object("entities") is null
            ? throw new DescriptorRefusal("It has no 'entities' object.")
            : descriptor;
    }

    private static DescriptorModel Model(string file, JsonObject descriptor, string @namespace)
    {
        var tenancyEnabled = descriptor.Object("tenancy")?.Bool("enabled") == true;
        var entities = descriptor.Object("entities")!.Members
            .Where(member => member.Value is JsonObject)
            .Select(member => EntityReader.Read(member.Key, (JsonObject)member.Value, tenancyEnabled));

        return new DescriptorModel(
            file, descriptor.String("name") ?? Stem(file), @namespace, Roles(descriptor), entities.ToImmutableArray());
    }

    private static ImmutableArray<string> Roles(JsonObject descriptor)
    {
        var declared = descriptor.Object("auth")?.Strings("roles") ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        return _builtInRoles.Concat(declared).Where(seen.Add).ToImmutableArray();
    }

    private static string Stem(string file)
    {
        var dot = file.IndexOf('.');

        return dot > 0 ? file.Substring(0, dot) : file;
    }
}

/// <summary>What <see cref="DescriptorReader.Read"/> answers: the model, or the reason it could not be read and where.</summary>
/// <param name="File">The descriptor's file name.</param>
/// <param name="Model">The model; <see langword="null"/> when <paramref name="Error"/> is set.</param>
/// <param name="Error">Why the file cannot be read, for ALVO2001.</param>
/// <param name="Line">The 1-based line of malformed JSON in the file; 0 when the reason has no position in the file.</param>
/// <param name="Column">The 1-based column of malformed JSON in the file; 0 when the reason has no position in the file.</param>
internal sealed record DescriptorReadResult(string File, DescriptorModel? Model, string? Error, int Line, int Column);

/// <summary>A reason a descriptor cannot be read, carried out of the read; never escapes <see cref="DescriptorReader"/>.</summary>
internal sealed class DescriptorRefusal(string message, int line = 0, int column = 0) : Exception(message)
{
    internal int Line { get; } = line;

    internal int Column { get; } = column;
}
