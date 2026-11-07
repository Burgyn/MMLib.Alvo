using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>An additional file marked <c>AlvoDescriptor="true"</c>: its path, its text and its <c>AlvoNamespace</c>.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Text">The file's text.</param>
/// <param name="Namespace">The file's <c>AlvoNamespace</c>; <see langword="null"/> when absent or blank.</param>
internal readonly record struct DescriptorFile(string Path, string Text, string? Namespace)
{
    internal const string DescriptorMetadata = "build_metadata.AdditionalFiles.AlvoDescriptor";

    internal const string NamespaceMetadata = "build_metadata.AdditionalFiles.AlvoNamespace";

    /// <summary>The file, or <see langword="null"/> when it is not marked as a descriptor or has no text.</summary>
    internal static DescriptorFile? From(AdditionalText file, AnalyzerConfigOptions options, CancellationToken ct)
    {
        if (!options.TryGetValue(DescriptorMetadata, out var marked) || !string.Equals(marked.Trim(), "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return file.GetText(ct)?.ToString() is { } text ? new DescriptorFile(file.Path, text, NamespaceOf(options)) : null;
    }

    private static string? NamespaceOf(AnalyzerConfigOptions options) =>
        options.TryGetValue(NamespaceMetadata, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}

/// <summary>One file's read: its path, for ordering and locations, and what the reader answered.</summary>
/// <param name="Path">The file's path.</param>
/// <param name="Result">The model, or the reason it could not be read.</param>
internal sealed record DescriptorRead(string Path, DescriptorReadResult Result);
