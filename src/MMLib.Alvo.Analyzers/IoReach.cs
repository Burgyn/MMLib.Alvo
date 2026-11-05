using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Collections.Immutable;

namespace MMLib.Alvo.Analyzers;

/// <summary>What an operation reaches that in-transaction host code must not: the boot ban's list, plus files.</summary>
internal static class IoReach
{
    /// <summary>The types reached by name, with their subclasses and implementations — <c>HookDependencies</c>' list and more.</summary>
    internal static ImmutableArray<string> BannedTypes { get; } =
    [
        "System.Data.Common.DbConnection", "System.Data.Common.DbDataSource", "System.Data.IDbConnection",
        "MMLib.Alvo.Data.IAlvoData", "MMLib.Alvo.Data.IAlvoSessions", "MMLib.Alvo.Data.IAlvoTransactions",
        "MMLib.Alvo.Events.IAlvoEvents", "MMLib.Alvo.Events.IOutboxStore", "MMLib.Alvo.Events.IDeliveryStore",
        "MMLib.Alvo.Events.IAlvoEventPump", "MMLib.Alvo.Commands.IAlvoCommands", "MMLib.Alvo.Management.IAlvoManagement",
        "MMLib.Alvo.Management.IAlvoOperations", "MMLib.Alvo.IAlvoUserStore", "MMLib.Alvo.IAlvoUserAdministration",
        "MMLib.Alvo.IAlvoBootstrapAdmin", "MMLib.Alvo.Ai.IAlvoAssistant",
        "Microsoft.EntityFrameworkCore.DbContext", "Microsoft.EntityFrameworkCore.IDbContextFactory`1",
    ];

    /// <summary>Every type of these namespaces is reached.</summary>
    internal static ImmutableArray<string> NetworkNamespaces { get; } =
        ["System.Net.Http", "System.Net.Sockets", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic"];

    /// <summary>The <c>System.Net</c> types reached by name, as the boot ban names them.</summary>
    internal static ImmutableHashSet<string> WebRequestTypes { get; } =
        ImmutableHashSet.Create(StringComparer.Ordinal, "WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest");

    private static readonly ImmutableHashSet<string> _fileTypes =
        ImmutableHashSet.Create(StringComparer.Ordinal, "File", "FileInfo", "FileStream", "Directory", "DirectoryInfo");

    /// <summary>Every namespace starting with one of these is reached — the boot ban's own <c>StartsWith</c> rule.</summary>
    internal static ImmutableArray<string> IdentityPrefixes { get; } = ["Microsoft.AspNetCore.Identity", "MMLib.Alvo.Identity"];

    /// <summary>Whether <paramref name="type"/> — or an element, generic argument, base type or interface of it — is reached.</summary>
    internal static bool IsReached(ITypeSymbol? type) => type switch
    {
        null => false,
        IArrayTypeSymbol array => IsReached(array.ElementType),
        INamedTypeSymbol { IsGenericType: true } named when named.TypeArguments.Any(IsReached) => true,
        _ => IsReachedItself(type),
    };

    /// <summary>The clause a diagnostic opens with, or <see langword="null"/> when <paramref name="operation"/> reaches nothing.</summary>
    internal static string? Describe(IOperation operation, AnalysisScope scope, AlvoSymbols symbols) => operation switch
    {
        IObjectCreationOperation creation when IsReached(creation.Type) => $"'{Name(creation.Type)}' is created",
        IFieldReferenceOperation { Field.HasConstantValue: true } => null,
        IFieldReferenceOperation { Field.IsStatic: true, Type.IsValueType: true } => null,
        IPropertyReferenceOperation { Property.IsStatic: true, Type.IsValueType: true } => null,
        IFieldReferenceOperation field when IsReached(field.Type) => $"'{Name(field.Type)}' is used",
        IPropertyReferenceOperation property when IsReached(property.Type) => $"'{Name(property.Type)}' is used",
        IParameterReferenceOperation parameter when IsReached(parameter.Type) => $"'{Name(parameter.Type)}' is used",
        ILocalReferenceOperation local when IsReached(local.Type) => $"'{Name(local.Type)}' is used",
        ITypeOfOperation typeOf when IsReached(typeOf.TypeOperand) => $"'{Name(typeOf.TypeOperand)}' is looked up",
        IAwaitOperation wait when !scope.IsCelFunction && AwaitedCall(wait.Operation) is { } awaited
            && !HasReachedReceiver(awaited) && !symbols.IsTransactionCall(awaited.TargetMethod, scope) =>
            $"'{Name(awaited.TargetMethod.ContainingType)}.{awaited.TargetMethod.Name}' is awaited, and only the write's transaction may be",
        IInvocationOperation call when scope.IsCelFunction && ReturnsTask(call.TargetMethod) && !HasReachedReceiver(call) =>
            $"'{Name(call.TargetMethod.ContainingType)}.{call.TargetMethod.Name}' returns a task, which a synchronous CEL function can only block on",
        IInvocationOperation call => DescribeCall(call),
        _ => null,
    };

    private static string? DescribeCall(IInvocationOperation call)
    {
        var method = call.TargetMethod;
        if (call.Instance is null && !method.IsExtensionMethod && IsReached(method.ContainingType))
        {
            return $"'{Name(method.ContainingType)}.{method.Name}' is called";
        }

        return method.TypeArguments.FirstOrDefault(IsReached) is { } resolved ? $"'{Name(resolved)}' is resolved" : null;
    }

    private static IInvocationOperation? AwaitedCall(IOperation operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation is IInvocationOperation { TargetMethod.Name: "ConfigureAwait", Instance: { } inner } ? AwaitedCall(inner) : operation as IInvocationOperation;
    }

    private static bool HasReachedReceiver(IInvocationOperation call) =>
        IsReached(call.Instance?.Type)
        || (call.TargetMethod.IsExtensionMethod && call.Arguments.Length > 0 && IsReached(call.Arguments[0].Value.Type));

    private static bool ReturnsTask(IMethodSymbol method) =>
        method.ReturnType is INamedTypeSymbol { ContainingNamespace: { } space } returned
        && space.ToDisplayString() == "System.Threading.Tasks"
        && returned.Name is "Task" or "ValueTask";

    private static bool IsReachedItself(ITypeSymbol type) => SelfAndAncestors(type).Any(IsBannedByItself);

    private static bool IsBannedByItself(ITypeSymbol type)
    {
        var space = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return NetworkNamespaces.Contains(space)
            || (space == "System.Net" && WebRequestTypes.Contains(type.Name))
            || (space == "System.IO" && _fileTypes.Contains(type.Name))
            || IdentityPrefixes.Any(prefix => space.StartsWith(prefix, StringComparison.Ordinal))
            || type.Name == "IEmailSender"
            || BannedTypes.Contains(MetadataName(type.OriginalDefinition));
    }

    private static IEnumerable<ITypeSymbol> SelfAndAncestors(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }

        foreach (var contract in type.AllInterfaces)
        {
            yield return contract;
        }
    }

    private static string MetadataName(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } space ? $"{space.ToDisplayString()}.{type.MetadataName}" : type.MetadataName;

    private static string Name(ITypeSymbol? type) => type?.Name ?? "?";
}
