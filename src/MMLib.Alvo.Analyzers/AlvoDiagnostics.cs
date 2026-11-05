using Microsoft.CodeAnalysis;

namespace MMLib.Alvo.Analyzers;

/// <summary>The diagnostics Alvo's analyzers report.</summary>
internal static class AlvoDiagnostics
{
    /// <summary>The page every rule's help link points into.</summary>
    internal const string HelpRoot = "https://alvo.burgyn.online/extending/analyzer-rules/";

    /// <summary>
    /// The descriptor behind <see cref="Alvo1001"/>: a field, because the release-tracking analyzer (RS2002) matches
    /// <c>AnalyzerReleases.Unshipped.md</c> against descriptors created in field initializers only.
    /// </summary>
    private static readonly DiagnosticDescriptor _alvo1001 = new(
        id: "ALVO1001",
        title: "In-transaction host code reaches the network, a database connection, a file or another data port",
        messageFormat: "{0} inside {1}, which runs while the write's transaction holds its locks; {2}",
        category: "Reliability",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Before-write, after-write, before-delete and after-delete hooks and CEL functions registered with AddCelFunction run inside the write's transaction. A network call, a database connection, a file or a second Alvo data port there holds the write's row locks for as long as it takes, and does work a rollback cannot undo. Validate hooks and event handlers are where such calls belong.",
        helpLinkUri: HelpRoot + "#alvo1001");

    /// <summary>Gets the rule: in-transaction host code reaches the network, a database connection, a file or another data port.</summary>
    internal static DiagnosticDescriptor Alvo1001 => _alvo1001;
}
