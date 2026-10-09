using MMLib.Alvo.Expressions;
using MMLib.Alvo.Expressions.Internal;
using System.Linq.Expressions;
using System.Reflection.Emit;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>Delegate shapes whose <c>Method</c> does not mirror the delegate type: nullability must fall to the safe side, never crash.</summary>
public sealed class CelDelegateShapeTests
{
    private static int _calls;

    private static string Count(string value)
    {
        _calls++;
        return value;
    }

    private static string NotNullable(string value) => value;

    private static string? Nullable(string? value) => value;

    private static Delegate Closed() => (Func<string, string>)"pre".Cat;

    private static Delegate OpenInstance() =>
        Delegate.CreateDelegate(typeof(Func<string, string>), typeof(string).GetMethod("Trim", Type.EmptyTypes)!);

    private static Delegate Compiled(bool annotated)
    {
        Expression<Func<string, string>> plain = s => Count(s);
        Expression<Func<string?, string>> loose = s => Count(s!);
        return annotated ? loose.Compile() : plain.Compile();
    }

    private static Delegate Dynamic()
    {
        var method = new DynamicMethod("dyn", typeof(string), [typeof(string)]);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, typeof(CelDelegateShapeTests).GetMethod(nameof(Count), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate(typeof(Func<string, string>));
    }

    public static TheoryData<string, Delegate> Shapes() => new()
    {
        { "closed extension method", Closed() },
        { "open instance string.Trim", OpenInstance() },
        { "compiled expression, string?", Compiled(annotated: true) },
        { "compiled expression, string", Compiled(annotated: false) },
        { "dynamic method", Dynamic() },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_null_argument_to_a_non_nullable_parameter_yields_null_without_invoking(string shape, Delegate function)
    {
        _ = shape;
        Ext.Calls = 0;
        _calls = 0;
        var catalogued = HostCelFunction.Create("probe", function, summary: null);

        catalogued.Parameters.Single().Nullable.ShouldBeFalse();
        catalogued.Invoke([null]).ShouldBeNull();
        (_calls + Ext.Calls).ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void A_registered_odd_shape_still_runs_with_a_value(string shape, Delegate function)
    {
        _ = shape;

        HostCelFunction.Create("probe", function, summary: null).Invoke(["  a "]).ShouldNotBeNull();
    }

    [Fact]
    public void An_annotated_non_null_result_is_not_null() =>
        HostCelFunction.Create("probe", NotNullable, summary: null).ResultNullable.ShouldBeFalse();

    [Fact]
    public void An_annotated_nullable_result_may_be_null() =>
        HostCelFunction.Create("probe", Nullable, summary: null).ResultNullable.ShouldBeTrue();

    [Fact]
    public void A_nullable_lambda_result_may_be_null() =>
        HostCelFunction.Create("probe", (string? d) => d, summary: null).ResultNullable.ShouldBeTrue();

    [Fact]
    public void A_value_type_result_is_never_null_unless_it_is_nullable()
    {
        HostCelFunction.Create("a", (int i) => i, summary: null).ResultNullable.ShouldBeFalse();
        HostCelFunction.Create("b", (int i) => (int?)i, summary: null).ResultNullable.ShouldBeTrue();
    }

    [Fact]
    public void An_oblivious_result_may_be_null() =>
        HostCelFunction.Create("probe", CelObliviousDelegates.Parameterised(), summary: null).ResultNullable.ShouldBeTrue();

    [Fact]
    public void An_oblivious_parameter_reads_as_non_nullable_and_is_not_invoked_with_null()
    {
        CelObliviousDelegates.Calls = 0;
        var catalogued = HostCelFunction.Create("probe", CelObliviousDelegates.Parameterised(), summary: null);

        catalogued.Parameters.Single().Nullable.ShouldBeFalse();
        catalogued.Invoke([null]).ShouldBeNull();
        CelObliviousDelegates.Calls.ShouldBe(0);
    }

    [Fact]
    public void A_dynamic_method_result_with_no_return_parameter_may_be_null() =>
        HostCelFunction.Create("probe", Dynamic(), summary: null).ResultNullable.ShouldBeTrue();
}

/// <summary>An extension method to close a delegate over.</summary>
internal static class Ext
{
    internal static int Calls { get; set; }

    internal static string Cat(this string prefix, string value)
    {
        Calls++;
        return prefix + value;
    }
}
