using Microsoft.CodeAnalysis;

namespace MMLib.Alvo.Analyzers.Generator;

/// <summary>The diagnostics the typed-entity generator reports.</summary>
internal static class GeneratorDiagnostics
{
    /// <summary>The page every generator rule's help link points into.</summary>
    internal const string HelpRoot = "https://alvo.burgyn.online/extending/generated-types/";

    internal const string Category = "Alvo.Generator";

    private static readonly DiagnosticDescriptor _unreadable = new(
        id: "ALVO2001",
        title: "An AlvoDescriptor file is not an Alvo descriptor the generator can read",
        messageFormat: "'{0}' is not an Alvo descriptor the generator can read: {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A file marked AlvoDescriptor=\"true\" must be an Alvo descriptor (apiVersion alvo.dev/v1, an entities object) or a Management API descriptor snapshot carrying one in descriptorJson.",
        helpLinkUri: HelpRoot + "#alvo2001");

    private static readonly DiagnosticDescriptor _collision = new(
        id: "ALVO2002",
        title: "Two entities or descriptors would generate the same type",
        messageFormat: "'{0}' would generate '{1}', which another entity or descriptor already generates; it is skipped. Rename the entity, or set AlvoNamespace on one of the descriptors.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every generated type name is taken once per namespace. The descriptor read first (by path) keeps the name; a later entity or roles class that needs it is not generated.",
        helpLinkUri: HelpRoot + "#alvo2002");

    /// <summary>Gets ALVO2001: the file is not a descriptor the generator can read.</summary>
    internal static DiagnosticDescriptor Unreadable => _unreadable;

    /// <summary>Gets ALVO2002: a generated type name is already taken.</summary>
    internal static DiagnosticDescriptor Collision => _collision;
}
