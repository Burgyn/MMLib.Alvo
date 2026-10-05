using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Registering a host function: what is accepted, what is refused at the call, and what discovery shows.</summary>
public sealed class CelFunctionRegistrationTests
{
    private delegate string ByRef(ref string value);

    private static ServiceProvider Build(Action<IAlvoBuilder> register)
    {
        var services = new ServiceCollection();
        register(services.AddAlvo());
        return services.BuildServiceProvider();
    }

    private static IAlvoBuilder Alvo() => new ServiceCollection().AddAlvo();

    [Fact]
    public void A_registered_function_compiles_and_runs_in_a_mutate()
    {
        using var provider = Build(alvo => alvo.AddCelFunction(
            "normalizePhone", (string phone) => phone.Replace(" ", string.Empty, StringComparison.Ordinal), "Strips spaces."));

        var compiled = provider.GetRequiredService<ICelCompiler>().Compile("normalizePhone(name)", CelProfile.Mutate, TestCelFunctions.Items);

        compiled.IsSuccess.ShouldBeTrue();
        CelInterpreter.EvaluateMutation(compiled.Expression!, CelFixtures.Row(("name", "+421 900")), null, DateTimeOffset.UnixEpoch)
            .ShouldBe("+421900");
    }

    [Fact]
    public void A_compiler_built_without_the_container_knows_no_host_function() =>
        new CelCompiler().Compile("normalizePhone(name)", CelProfile.Mutate, TestCelFunctions.Items).Errors.ShouldHaveSingleItem()
            .Message.ShouldBe("'normalizePhone' is not a recognized function.");

    [Theory]
    [InlineData("Normalize")]
    [InlineData("1abc")]
    [InlineData("normalize-phone")]
    [InlineData("normalizéPhone")]
    [InlineData("abc\n")]
    [InlineData("has")]
    [InlineData("changed")]
    [InlineData("now")]
    [InlineData("lowerAscii")]
    [InlineData("trim")]
    [InlineData("abs")]
    [InlineData("in")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("old")]
    [InlineData("new")]
    [InlineData("exists")]
    [InlineData("while")]
    public void A_name_the_catalog_cannot_honour_is_refused_at_registration(string name) =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction(name, (string s) => s)).Message.ShouldContain(name.TrimEnd('\n'));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction(name, (string s) => s));

    [Theory]
    [InlineData("vat_rate")]
    [InlineData("normalizePhone")]
    [InlineData("a")]
    [InlineData("x9")]
    public void A_lower_camel_or_snake_name_is_accepted(string name) =>
        Should.NotThrow(() => Alvo().AddCelFunction(name, (string s) => s));

    [Fact]
    public void A_second_registration_of_a_name_is_refused()
    {
        var alvo = Alvo().AddCelFunction("vat_rate", (string country) => 0.2m);

        Should.Throw<ArgumentException>(() => alvo.AddCelFunction("vat_rate", (string country) => 0.1m)).Message.ShouldContain("already registered");
    }

    [Fact]
    public void The_same_delegate_under_two_names_is_two_functions()
    {
        Func<string, string> shared = s => s;
        using var provider = Build(alvo => alvo.AddCelFunction("first", shared).AddCelFunction("second", shared));

        provider.GetRequiredService<CelFunctionCatalog>().Describe().Count(f => f.Provenance == CelFunctionProvenance.Host).ShouldBe(2);
    }

    [Theory]
    [InlineData("exists_one")]
    [InlineData("map")]
    [InlineData("filter")]
    [InlineData("all")]
    [InlineData("replace")]
    [InlineData("round")]
    [InlineData("size")]
    [InlineData("false")]
    public void A_macro_or_built_in_name_is_refused(string name) =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction(name, (string s) => s)).Message.ShouldContain(name);

    public static TheoryData<string, Delegate> Unsupported() => new()
    {
        { "a DateTime parameter", (DateTime value) => "x" },
        { "an object parameter", (object value) => "x" },
        { "five parameters", (string a, string b, string c, string d, string e) => a },
        { "no result", (Action<string>)(_ => { }) },
        { "a Task result", (string value) => Task.FromResult(value) },
        { "a ValueTask result", (string value) => ValueTask.FromResult(value) },
        { "an object result", (string value) => (object)value },
        { "a ref parameter", (ByRef)((ref string value) => value) },
        { "a multicast delegate", Multicast() },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public void A_delegate_the_catalog_cannot_call_is_refused_at_registration(string why, Delegate function)
    {
        _ = why;

        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction("probe", function));
    }

    [Fact]
    public void An_asynchronous_function_is_told_where_io_belongs() =>
        Should.Throw<ArgumentException>(() => Alvo().AddCelFunction("probe", (string value) => Task.FromResult(value)))
            .Message.ShouldContain("after-hook");

    [Fact]
    public void Every_supported_type_is_described_with_its_cel_type_and_nullability()
    {
        HostCelFunction.Create("first", (string s, long l, int? i, decimal? d) => s, summary: null).Parameters
            .Select(p => (p.Name, p.Type, p.Nullable))
            .ShouldBe([("s", CelValueType.String, false), ("l", CelValueType.Int, false), ("i", CelValueType.Int, true), ("d", CelValueType.Decimal, true)]);
        HostCelFunction.Create("second", (bool b, DateTimeOffset t, Guid g, string? n) => n, summary: null).Parameters
            .Select(p => (p.Type, p.Nullable))
            .ShouldBe([(CelValueType.Bool, false), (CelValueType.Timestamp, false), (CelValueType.Uuid, false), (CelValueType.String, true)]);
    }

    [Fact]
    public void A_registration_after_the_host_is_built_is_refused_by_the_read_only_collection()
    {
        var services = new ServiceCollection();
        var alvo = services.AddAlvo();
        services.MakeReadOnly();

        Should.Throw<InvalidOperationException>(() => alvo.AddCelFunction("late", (string s) => s));
    }

    [Fact]
    public void Discovery_shows_a_host_function_among_the_built_ins()
    {
        using var provider = Build(alvo => alvo.AddCelFunction("normalizePhone", (string phone) => phone, "Keeps the digits."));

        var described = provider.GetRequiredService<CelFunctionCatalog>().Describe();

        var host = described.Single(f => f.Provenance == CelFunctionProvenance.Host);
        host.Name.ShouldBe("normalizePhone");
        host.Summary.ShouldBe("Keeps the digits.");
        host.Result.ShouldBe(CelValueType.String);
        host.Profiles.ShouldBe([CelProfile.Condition, CelProfile.Mutate]);
        host.Parameters.ShouldHaveSingleItem().Name.ShouldBe("phone");
        described.Select(f => f.Name).ShouldBe(described.Select(f => f.Name).Order(StringComparer.Ordinal));
        described.Where(f => f.Provenance == CelFunctionProvenance.BuiltIn).Select(f => f.Name).Distinct()
            .ShouldBe(["abs", "lowerAscii", "now", "replace", "round", "size", "trim"]);
    }

    private static Delegate Multicast()
    {
        Func<string, string> first = s => s;
        Func<string, string> second = s => s;
        return first + second;
    }
}
