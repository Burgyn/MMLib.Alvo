using Microsoft.CodeAnalysis;

namespace MMLib.Alvo.Analyzers;

/// <summary>Where an operation runs, as a diagnostic names it, and what to do instead.</summary>
internal sealed class AnalysisScope
{
    private const string HookFix = "call the service from a validate hook, which runs before the transaction, or from an event handler after the commit";
    private const string FunctionFix = "a CEL function must be synchronous and pure: read the value before the write, or check it in a validate hook";

    private AnalysisScope(string name, string fix, bool isCelFunction, INamedTypeSymbol? hookType, SyntaxNode? body = null)
    {
        Name = name;
        Fix = fix;
        IsCelFunction = isCelFunction;
        HookType = hookType;
        Body = body;
    }

    internal static AnalysisScope CelFunction { get; } = new("a CEL function registered with AddCelFunction", FunctionFix, isCelFunction: true, hookType: null);

    internal string Name { get; }

    internal string Fix { get; }

    internal bool IsCelFunction { get; }

    /// <summary>Gets the hook type whose own members (and nested types' members) count as the hook's code; none for a delegate.</summary>
    internal INamedTypeSymbol? HookType { get; }

    /// <summary>Gets the registered delegate's analysed body, whose local functions are the delegate's own code; none for a hook type.</summary>
    internal SyntaxNode? Body { get; }

    internal static AnalysisScope OfHookType(string stage, INamedTypeSymbol type) => new($"the {stage} hook '{type.Name}'", HookFix, isCelFunction: false, type);

    internal static AnalysisScope HookDelegate(string stage) => new($"a {stage} hook delegate", HookFix, isCelFunction: false, hookType: null);

    /// <summary>This scope, analysing <paramref name="body"/> — a registered delegate's lambda or method-group body.</summary>
    internal AnalysisScope Within(SyntaxNode body) => new(Name, Fix, IsCelFunction, HookType, body);

    /// <summary>
    /// Whether <paramref name="method"/> is the hook's own code — a member (or local function) of the hook type or a type nested
    /// in it, or a local function declared inside the analysed delegate body (Ruling 25).
    /// </summary>
    internal bool IsOwnCode(IMethodSymbol method) => IsLocalToBody(method) || IsMemberOfHook(method);

    private bool IsLocalToBody(IMethodSymbol method) =>
        method.MethodKind == MethodKind.LocalFunction && Body is not null
        && method.DeclaringSyntaxReferences.Any(reference => reference.SyntaxTree == Body.SyntaxTree && Body.Span.Contains(reference.Span));

    private bool IsMemberOfHook(IMethodSymbol method)
    {
        for (var type = method.ContainingType; type is not null && HookType is not null; type = type.ContainingType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, HookType.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }
}
