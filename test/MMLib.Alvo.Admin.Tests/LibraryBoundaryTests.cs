using Microsoft.AspNetCore.Components;
using System.Reflection;

namespace MMLib.Alvo.Admin.Tests;

/// <summary>
/// MudBlazor is how the dashboard is drawn, never part of what it promises (spec D5).
/// </summary>
/// <remarks>
/// Razor compiles every component public, so a Mud enum on a parameter would be a Mud type in
/// <c>PublicApi.MMLib.Alvo.Admin.verified.txt</c> — and every MudBlazor major (roughly yearly, study §1.1)
/// a breaking change of Alvo's. The wrappers take Alvo's own nested enums instead.
/// </remarks>
public sealed class LibraryBoundaryTests
{
    private const string Library = "MudBlazor";

    private static readonly Assembly _admin = typeof(AlvoAdmin).Assembly;

    [Fact]
    public void No_member_a_consumer_can_see_is_typed_by_the_library()
        => ContractTypes()
            .SelectMany(type => SignatureTypes(type).Select(signature => (type, signature)))
            .Where(pair => Mentions(pair.signature.Type))
            .Select(pair => $"{pair.type.Name}.{pair.signature.Member}")
            .ShouldBeEmpty("a MudBlazor type on a visible member makes every MudBlazor major a breaking change of Alvo");

    [Fact]
    public void No_public_component_derives_from_a_library_component()
        => PublicComponents()
            .Where(type => Ancestors(type).Any(IsLibrary))
            .Select(type => type.FullName)
            .ShouldBeEmpty();

    /// <summary>
    /// The text half of the pair: the reflection facts above say which members the check reads, this one says
    /// nothing slipped into what is approved.
    /// </summary>
    [Fact]
    public void The_approved_public_api_names_no_library_type()
        => File.ReadAllText(Path.Combine(
                RepositoryRoot.Find(), "test", "MMLib.Alvo.Admin.Tests", "PublicApi.MMLib.Alvo.Admin.verified.txt"))
            .ShouldNotContain(Library);

    [Fact]
    public void The_check_sees_a_library_type_inside_a_generic_argument_or_an_array()
    {
        Mentions(typeof(EventCallback<MudBlazor.Color>)).ShouldBeTrue();
        Mentions(typeof(MudBlazor.Severity[])).ShouldBeTrue();
        Mentions(typeof(IReadOnlyList<string>)).ShouldBeFalse();
    }

    [Fact]
    public void The_check_reads_protected_members_fields_and_constructors_and_skips_private_ones()
        => SignatureTypes(typeof(Specimen))
            .Where(signature => Mentions(signature.Type))
            .Select(signature => signature.Member)
            .ShouldBe(["Hue", "Paint", "Paint(severity)", "Shade", ".ctor(color)"], ignoreOrder: true);

    [Fact]
    public void The_check_covers_a_type_nested_in_a_component()
        => ContractTypes().ShouldContain(typeof(Components.DesignSystem.AlvoButton.ButtonTone));

    private static IEnumerable<Type> PublicComponents()
        => _admin.GetExportedTypes().Where(IsComponent);

    /// <summary>The components, and every public type nested inside one, such as a wrapper's own enum.</summary>
    private static IEnumerable<Type> ContractTypes()
        => _admin.GetExportedTypes().Where(type => IsComponent(type) || Enclosing(type).Any(IsComponent));

    private static bool IsComponent(Type type) => typeof(IComponent).IsAssignableFrom(type);

    private static IEnumerable<Type> Enclosing(Type type)
    {
        for (var current = type.DeclaringType; current is not null; current = current.DeclaringType)
        {
            yield return current;
        }
    }

    /// <summary>What a consumer can see: public members, and the protected ones a subclass reaches.</summary>
    private static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var property in type.GetProperties(declared).Where(property => property.GetAccessors(true).Any(Visible)))
        {
            yield return (property.Name, property.PropertyType);
        }

        foreach (var field in type.GetFields(declared).Where(field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
        {
            yield return (field.Name, field.FieldType);
        }

        foreach (var method in type.GetMethods(declared).Where(method => !method.IsSpecialName && Visible(method)))
        {
            yield return (method.Name, method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{method.Name}({parameter.Name})", parameter.ParameterType);
            }
        }

        foreach (var constructor in type.GetConstructors(declared).Where(Visible))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return ($".ctor({parameter.Name})", parameter.ParameterType);
            }
        }
    }

    private static bool Visible(MethodBase method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static IEnumerable<Type> Ancestors(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static bool IsLibrary(Type type) => type.Assembly.GetName().Name == Library;

    private static bool Mentions(Type type)
        => IsLibrary(type)
            || (type.HasElementType && Mentions(type.GetElementType()!))
            || type.GetGenericArguments().Any(Mentions);

    /// <summary>A component shaped to prove what the check reads; it is not part of the admin assembly.</summary>
    private abstract class Specimen : ComponentBase
    {
        protected Specimen(MudBlazor.Color color)
            : this(MudBlazor.Size.Small, color)
        {
        }

        private Specimen(MudBlazor.Size size, MudBlazor.Color color)
        {
            Hidden = size;
            Hue = color;
        }

        public MudBlazor.Color Hue { get; set; }

        protected MudBlazor.Variant Shade { get; set; }

        private MudBlazor.Size Hidden { get; }

        protected MudBlazor.Color Paint(MudBlazor.Severity severity)
            => Conceal() == MudBlazor.Size.Small && severity == MudBlazor.Severity.Info ? Hue : MudBlazor.Color.Default;

        private MudBlazor.Size Conceal() => Hidden;
    }
}
