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
    public void No_public_member_of_a_component_is_typed_by_the_library()
        => PublicComponents()
            .SelectMany(type => SignatureTypes(type).Select(signature => (type, signature)))
            .Where(pair => Mentions(pair.signature.Type))
            .Select(pair => $"{pair.type.Name}.{pair.signature.Member}")
            .ShouldBeEmpty("a MudBlazor type on a public member makes every MudBlazor major a breaking change of Alvo");

    [Fact]
    public void No_public_component_derives_from_a_library_component()
        => PublicComponents()
            .Where(type => Ancestors(type).Any(IsLibrary))
            .Select(type => type.FullName)
            .ShouldBeEmpty();

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

    private static IEnumerable<Type> PublicComponents()
        => _admin.GetExportedTypes().Where(type => typeof(IComponent).IsAssignableFrom(type));

    private static IEnumerable<(string Member, Type Type)> SignatureTypes(Type type)
    {
        const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var property in type.GetProperties(declared))
        {
            yield return (property.Name, property.PropertyType);
        }

        foreach (var method in type.GetMethods(declared).Where(method => !method.IsSpecialName))
        {
            yield return (method.Name, method.ReturnType);
            foreach (var parameter in method.GetParameters())
            {
                yield return ($"{method.Name}({parameter.Name})", parameter.ParameterType);
            }
        }
    }

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
}
