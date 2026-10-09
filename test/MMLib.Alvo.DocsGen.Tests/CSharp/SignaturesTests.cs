using MMLib.Alvo.Api;
using MMLib.Alvo.Auth;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.DocsGen.Tests.CSharp;

public class SignaturesTests
{
    [Fact]
    public void A_settable_property()
    {
        Signatures.Of(typeof(AlvoApiOptions).GetProperty(nameof(AlvoApiOptions.RoutePrefix))!)
            .ShouldBe("public string RoutePrefix { get; set; }");
    }

    [Fact]
    public void An_init_property()
    {
        Signatures.Of(typeof(AlvoAuthOptions).GetProperty(nameof(AlvoAuthOptions.HeaderName))!)
            .ShouldBe("public string HeaderName { get; init; }");
    }

    [Fact]
    public void An_interface_method_with_a_default_cancellation_token()
    {
        Signatures.Of(typeof(IAlvoManagement).GetMethod(nameof(IAlvoManagement.GetCelFunctionsAsync))!)
            .ShouldBe("Task<ManagementCelFunctions> GetCelFunctionsAsync(string project, CancellationToken ct = default)");
    }

    [Fact]
    public void A_constant()
    {
        Signatures.Of(typeof(Shapes).GetField(nameof(Shapes.Prefix))!)
            .ShouldBe("public const string Prefix = \"alvo\";");
    }

    [Fact]
    public void A_generic_method_with_an_enum_default()
    {
        Signatures.Of(typeof(Shapes).GetMethod(nameof(Shapes.Pick))!)
            .ShouldBe("public static T Pick<T>(T value, StringComparison comparison = StringComparison.Ordinal, bool strict = true, int retries = 3)");
    }

    [Fact]
    public void A_constructor_and_a_get_only_nullable_property()
    {
        Signatures.Of(typeof(Shapes).GetConstructors().Single()).ShouldBe("public Shapes(string? name)");
        Signatures.Of(typeof(Shapes).GetProperty(nameof(Shapes.Name))!).ShouldBe("public string? Name { get; }");
    }

    public sealed class Shapes(string? name)
    {
        public const string Prefix = "alvo";

        public string? Name { get; } = name;

        public static T Pick<T>(T value, StringComparison comparison = StringComparison.Ordinal, bool strict = true, int retries = 3) =>
            strict && retries > 0 && comparison == StringComparison.Ordinal ? value : value;
    }
}
