using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>
/// ALVO1001: in-transaction host code — a before/after-write or before/after-delete hook, and a CEL function registered
/// with <c>AddCelFunction</c> — must not reach the network, a database connection, a file or another data port.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InTransactionIoAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<OperationKind> _reaching = ImmutableArray.Create(
        OperationKind.ObjectCreation, OperationKind.Invocation, OperationKind.TypeOf, OperationKind.FieldReference,
        OperationKind.PropertyReference, OperationKind.ParameterReference, OperationKind.LocalReference, OperationKind.Await);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(AlvoDiagnostics.Alvo1001);

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (AlvoSymbols.From(context.Compilation) is not { } symbols)
        {
            return;
        }

        context.RegisterOperationBlockStartAction(block => OnBlock(block, symbols));
        context.RegisterOperationAction(operation => OnRegistration(operation, symbols), OperationKind.Invocation);
    }

    private static void OnBlock(OperationBlockStartAnalysisContext block, AlvoSymbols symbols)
    {
        if (block.OwningSymbol.ContainingType is { } type && symbols.HookScopeOf(type) is { } scope)
        {
            var seen = new ReportedSymbols();
            block.RegisterOperationAction(operation => Report(operation.ReportDiagnostic, operation.Operation, scope, symbols, seen), _reaching);
        }
    }

    private static void OnRegistration(OperationAnalysisContext context, AlvoSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (symbols.RegistrationOf(invocation, out var registered) is not { } scope || Body(registered, invocation) is not { } body)
        {
            return;
        }

        var seen = new ReportedSymbols();
        var within = scope.Within(body.Syntax);
        foreach (var operation in body.DescendantsAndSelf())
        {
            Report(context.ReportDiagnostic, operation, within, symbols, seen);
        }
    }

    /// <summary>A lambda's body, or the body of a method group declared in the same file.</summary>
    private static IOperation? Body(IOperation? registered, IOperation invocation)
    {
        while (registered is IConversionOperation conversion)
        {
            registered = conversion.Operand;
        }

        if (registered is IDelegateCreationOperation creation)
        {
            registered = creation.Target;
        }

        return registered switch
        {
            IAnonymousFunctionOperation lambda => lambda.Body,
            IMethodReferenceOperation reference => SameFileBody(reference.Method, invocation),
            _ => null,
        };
    }

    private static IOperation? SameFileBody(IMethodSymbol method, IOperation invocation)
    {
        var declaration = method.DeclaringSyntaxReferences.FirstOrDefault(reference => reference.SyntaxTree == invocation.Syntax.SyntaxTree);
        return declaration is null ? null : invocation.SemanticModel?.GetOperation(declaration.GetSyntax());
    }

    private static void Report(Action<Diagnostic> report, IOperation operation, AnalysisScope scope, AlvoSymbols symbols, ReportedSymbols seen)
    {
        if (IoReach.Describe(operation, scope, symbols) is { } what && seen.FirstUse(operation))
        {
            report(Diagnostic.Create(AlvoDiagnostics.Alvo1001, operation.Syntax.GetLocation(), what, scope.Name, scope.Fix));
        }
    }
}
