using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.DocsGen.Xml;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MMLib.Alvo.DocsGen.CSharp;

internal static class ApiScope
{
    private const string OptionsSuffix = "Options";

    private const BindingFlags DeclaredPublic =
        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    internal static IReadOnlySet<string> Ports { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "IAlvoBuilder",
        "IAlvoData",
        "IAlvoManagement",
        "IAlvoUserAdministration",
        "IAlvoEvents",
        "IAlvoContextResolver",
        "IAlvoContextAccessor",
        "IAlvoAdminCallerResolver",
        "AlvoProblemTypes",
    };

    private static readonly Type[] _registrationTargets = [typeof(IServiceCollection), typeof(IAlvoBuilder), typeof(IEndpointRouteBuilder)];

    internal static IReadOnlyList<Type> TypesOf(Assembly assembly)
    {
        var exported = assembly.GetExportedTypes();
        var options = exported.Where(IsOptions).ToHashSet();
        var held = options.SelectMany(HeldTypes).Where(type => type.Assembly == assembly && type.IsClass && type.IsVisible).ToHashSet();
        return [.. exported
            .Where(type => IsRegistration(type) || options.Contains(type) || held.Contains(type) || Ports.Contains(type.Name))
            .OrderBy(type => type.Namespace, StringComparer.Ordinal)
            .ThenBy(type => TypeNames.Display(type, null), StringComparer.Ordinal)];
    }

    internal static IReadOnlyList<MemberInfo> MembersOf(Type type) =>
    [
        .. type.GetMembers(DeclaredPublic)
            .Where(IsDocumentedMember)
            .OrderBy(KindOrder)
            .ThenBy(member => member.Name, StringComparer.Ordinal)
            .ThenBy(Signatures.Of, StringComparer.Ordinal),
    ];

    internal static string DocIdOf(MemberInfo member) => member switch
    {
        MethodBase method => DocId.Of(method),
        PropertyInfo property => DocId.Of(property),
        FieldInfo field => DocId.Of(field),
        EventInfo @event => "E" + DocId.Of(@event.DeclaringType!)[1..] + "." + @event.Name,
        Type type => DocId.Of(type),
        _ => throw new ArgumentException($"No documentation id for a {member.MemberType} ('{member.Name}').", nameof(member)),
    };

    private static bool IsOptions(Type type) =>
        type.IsClass && !type.IsAbstract && type.Name.EndsWith(OptionsSuffix, StringComparison.Ordinal);

    private static bool IsRegistration(Type type) =>
        type is { IsAbstract: true, IsSealed: true }
        && type.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(IsRegistrationExtension);

    private static bool IsRegistrationExtension(MethodInfo method) =>
        method.IsDefined(typeof(ExtensionAttribute))
        && method.GetParameters() is [var target, ..]
        && _registrationTargets.Contains(target.ParameterType);

    private static IEnumerable<Type> HeldTypes(Type options) =>
        options.GetProperties(BindingFlags.Public | BindingFlags.Instance).SelectMany(property => Unwrap(property.PropertyType));

    private static IEnumerable<Type> Unwrap(Type type) =>
        type.IsArray ? Unwrap(type.GetElementType()!)
        : type.IsGenericType ? type.GetGenericArguments().SelectMany(Unwrap)
        : [type];

    private static bool IsDocumentedMember(MemberInfo member) =>
        !member.IsDefined(typeof(CompilerGeneratedAttribute))
        && member switch
        {
            MethodInfo method => !method.IsSpecialName && method.GetBaseDefinition().DeclaringType != typeof(object),
            ConstructorInfo constructor => constructor.GetParameters().Length > 0,
            PropertyInfo or FieldInfo or EventInfo => true,
            _ => false,
        };

    private static int KindOrder(MemberInfo member) => member.MemberType switch
    {
        MemberTypes.Constructor => 0,
        MemberTypes.Field => 1,
        MemberTypes.Property => 2,
        MemberTypes.Event => 3,
        _ => 4,
    };
}
