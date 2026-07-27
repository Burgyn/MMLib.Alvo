using NetArchTest.Rules;
using System.Reflection;

namespace MMLib.Alvo.Tests.Architecture;

/// <summary>
/// Finds every publicly reachable mention of EF Core's <c>DbContext</c>, <c>DbSet&lt;&gt;</c> or
/// <c>ChangeTracker</c> in an assembly's exported surface.
/// </summary>
/// <remarks>
/// <para>
/// Matched by full type <em>name</em> rather than by <c>typeof</c>, because this scan is linked into test
/// projects that have no EF Core reference of their own (Abstractions, Schema, the core) — and must keep
/// working if that stops being true, which it since has: <c>MMLib.Alvo.Testing</c> reaches
/// <c>MMLib.Alvo.Data.EntityFrameworkCore</c> for <c>IAlvoSqlDialect</c>, so every test project now resolves
/// EF Core Relational transitively. Not typing the names is what makes this rule independent of who can see
/// them. The cost of a name match is that an EF rename would make the rule silently vacuous, so
/// <c>MMLib.Alvo.Data.EntityFrameworkCore.Tests</c> — which references EF directly — carries a fact that
/// every name here still resolves to a real type.
/// </para>
/// <para>
/// Inherited members are included on purpose: a public type <em>deriving</em> from <c>DbContext</c> exposes
/// its change tracker without declaring a single member of its own.
/// </para>
/// </remarks>
internal static class EfSurfaceScan
{
    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly string[] _forbiddenTypeNames =
    [
        "Microsoft.EntityFrameworkCore.DbContext",
        "Microsoft.EntityFrameworkCore.DbSet`1",
        "Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker",
    ];

    /// <summary>The forbidden type names, so a project that can see EF may assert they still exist.</summary>
    internal static IReadOnlyList<string> ForbiddenTypeNames => _forbiddenTypeNames;

    /// <summary>Every offending member, formatted as <c>Type.Member</c>, in <paramref name="types"/>.</summary>
    /// <param name="types">The types whose externally visible surface to scan.</param>
    internal static IReadOnlyList<string> Offenders(IEnumerable<Type> types) =>
        [.. types.SelectMany(Offenders).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>How many externally visible members <paramref name="types"/> presented — the non-vacuity counter.</summary>
    /// <param name="types">The types whose externally visible surface to count.</param>
    internal static int VisibleMemberCount(IEnumerable<Type> types) =>
        types.SelectMany(VisibleMembers).Count();

    private static IEnumerable<string> Offenders(Type type) =>
        InheritedTypes(type).Where(IsForbidden).Select(_ => type.FullName ?? type.Name)
            .Concat(VisibleMembers(type)
                .Where(member => SignatureTypes(member).SelectMany(Expand).Any(IsForbidden))
                .Select(member => $"{type.FullName}.{member.Name}"));

    private static Type[] InheritedTypes(Type type) =>
        type.BaseType is { } baseType ? [baseType, .. type.GetInterfaces()] : type.GetInterfaces();

    private static IEnumerable<MemberInfo> VisibleMembers(Type type) =>
        type.GetMembers(AllMembers).Where(IsVisibleOutsideTheAssembly);

    private static bool IsForbidden(Type type) =>
        _forbiddenTypeNames.Contains(Normalize(type).FullName, StringComparer.Ordinal);

    private static Type Normalize(Type type) => type.IsGenericType ? type.GetGenericTypeDefinition() : type;

    /// <summary>A type plus every type reachable through it — generic arguments, array elements, by-ref targets.</summary>
    private static IEnumerable<Type> Expand(Type type) =>
    [
        type,
        .. type.HasElementType ? Expand(type.GetElementType()!) : [],
        .. type.GenericTypeArguments.SelectMany(Expand),
    ];

    private static bool IsVisibleOutsideTheAssembly(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        PropertyInfo property => (property.GetMethod ?? property.SetMethod) is { } accessor
            && (accessor.IsPublic || accessor.IsFamily || accessor.IsFamilyOrAssembly),
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        EventInfo @event => @event.AddMethod is { } add && (add.IsPublic || add.IsFamily || add.IsFamilyOrAssembly),
        _ => false,
    };

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        MethodInfo method => [method.ReturnType, .. method.GetParameters().Select(parameter => parameter.ParameterType)],
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        EventInfo @event => @event.EventHandlerType is { } handler ? [handler] : [],
        _ => [],
    };
}

/// <summary>
/// Architecture rules that must hold for EVERY MMLib.Alvo production assembly.
/// Linked (not referenced) into each test project via <c>test/Directory.Build.props</c>,
/// so it compiles into each test assembly and runs against that project's sibling
/// production assembly (<see cref="TestTarget"/>). Opt a project out with
/// <c>AlvoSharedArchTests=false</c> where it does not map 1:1 to a production
/// assembly (conventions, integration, e2e).
/// </summary>
public class SharedArchitectureRules
{
    private const string InternalNamespaceSegmentPattern = @"(^|\.)Internal(\.|$)";
    private const string CoreAssemblyName = "MMLib.Alvo";
    private const string AbstractionsAssemblyName = "MMLib.Alvo.Abstractions";

    [Fact]
    public void Public_types_do_not_live_in_internal_namespaces()
    {
        var result = Types.InAssembly(TestTarget.Resolve())
            .That().ResideInNamespaceMatching(InternalNamespaceSegmentPattern)
            .ShouldNot().BePublic()
            .GetResult();

        var offenders = (result.FailingTypes ?? Enumerable.Empty<Type>()).Select(type => type.FullName);
        result.IsSuccessful.ShouldBeTrue(
            "Types in a '*.Internal' namespace must not be public. Offending types: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The EF-shield: the core must reference only <c>MMLib.Alvo.Abstractions</c>
    /// plus framework/system assemblies — never EF Core or Npgsql. EF lives
    /// exclusively in the Data.* packages behind ISchemaMigrator.
    /// </summary>
    /// <remarks>
    /// Unlike the other facts here, this invariant is about one specific
    /// assembly (<c>MMLib.Alvo</c>), not "whichever sibling this test project
    /// targets". It still resolves via <see cref="TestTarget"/> — the sibling
    /// that every other shared-arch-enabled project already has a working
    /// reference to — and no-ops unless that sibling is the core itself, so
    /// it does not attempt to <c>Assembly.Load("MMLib.Alvo")</c> from test
    /// projects (e.g. Schema.Tests, Abstractions.Tests) that never reference
    /// it and would fail to resolve it.
    /// </remarks>
    [Fact]
    public void Core_depends_only_on_Abstractions()
    {
        var core = TestTarget.Resolve();
        if (core.GetName().Name != CoreAssemblyName)
        {
            return;
        }

        var referencedAssemblyNames = core.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        var forbiddenEfReferences = referencedAssemblyNames
            .Where(name =>
                name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || name.StartsWith("Npgsql", StringComparison.Ordinal))
            .ToArray();

        forbiddenEfReferences.ShouldBeEmpty(
            $"{CoreAssemblyName} must stay EF-free — EF lives only in Data.* packages behind "
            + $"ISchemaMigrator. Offending references: {string.Join(", ", forbiddenEfReferences)}.");

        var unexpectedFamilyReferences = referencedAssemblyNames
            .Where(name => name.StartsWith(CoreAssemblyName, StringComparison.Ordinal))
            .Where(name => name != AbstractionsAssemblyName)
            .Where(name => name != CoreAssemblyName)
            .ToArray();

        unexpectedFamilyReferences.ShouldBeEmpty(
            $"{CoreAssemblyName} must depend on no other MMLib.Alvo.* assembly besides "
            + $"{AbstractionsAssemblyName}. Offending references: "
            + string.Join(", ", unexpectedFamilyReferences));
    }

    /// <summary>
    /// No Alvo assembly may hand EF Core's <c>DbContext</c>, <c>DbSet&lt;&gt;</c> or <c>ChangeTracker</c> to a
    /// caller. This is a security boundary, not encapsulation taste: a tracked, mutated property bag saved
    /// through the change tracker emits <c>UPDATE … WHERE id = @p</c> with <b>no policy predicate</b> — the
    /// shortest and most idiomatic EF code available, and a complete authorization bypass (spike <c>Q5d</c>).
    /// A caller who can reach a context can write around every rule the data path enforces.
    /// </summary>
    /// <remarks>
    /// Family-wide rather than data-path-local on purpose: the invariant is "nowhere", so a package that
    /// starts referencing EF later inherits the rule instead of needing to remember it.
    /// </remarks>
    [Fact]
    public void No_public_surface_exposes_efs_context_or_change_tracker()
    {
        var types = TestTarget.Resolve().GetExportedTypes();

        EfSurfaceScan.VisibleMemberCount(types).ShouldBeGreaterThan(
            0, "The scan found no externally visible member at all, so an empty offender list proves nothing.");

        var offenders = EfSurfaceScan.Offenders(types);

        offenders.ShouldBeEmpty(
            "No public or protected member of an Alvo assembly may expose EF's DbContext, DbSet or "
            + $"ChangeTracker — a caller holding one writes around policy. Offenders: {string.Join(", ", offenders)}.");
    }
}
