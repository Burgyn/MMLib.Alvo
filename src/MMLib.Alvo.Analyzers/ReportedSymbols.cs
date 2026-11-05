using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Analyzers;

/// <summary>The reached symbols one body has already reported, so a field, property, parameter or local is reported at its first use only.</summary>
internal sealed class ReportedSymbols
{
    private readonly ConcurrentDictionary<ISymbol, byte> _reported = new(SymbolEqualityComparer.Default);

    /// <summary>Whether <paramref name="operation"/> should be reported: anything but a symbol reference always; a reference the first time.</summary>
    internal bool FirstUse(IOperation operation) => SymbolOf(operation) is not { } symbol || _reported.TryAdd(symbol, 0);

    private static ISymbol? SymbolOf(IOperation operation) => operation switch
    {
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        IParameterReferenceOperation parameter => parameter.Parameter,
        ILocalReferenceOperation local => local.Local,
        _ => null,
    };
}
