using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="ComputedColumnSql"/> on its own, rather than through the model builder that calls it: the
/// argument guards, the refusal of a <c>computed</c> that does not compile, and the refusal of one that
/// renders a bind parameter DDL cannot carry.
/// </summary>
/// <remarks>
/// <para>
/// The compiler and renderer come out of <c>AddAlvo()</c>, as they do in <c>DescriptorModelBuilderTests</c>
/// — a fake compiler would prove only that this type passes on whatever it is handed. A stub compiler is
/// used for exactly one thing the real one cannot be made to produce on demand: a failure carrying a
/// specific fix suggestion.
/// </para>
/// <para>
/// <b>Only the contract-carrying substrings of a refusal are asserted</b> — the field it is about, the
/// expression its author wrote, the constant that made it refusable. The advice that follows is prose, and
/// asserting it would freeze the wording rather than the behaviour.
/// </para>
/// </remarks>
public sealed class ComputedColumnSqlTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    /// <summary>Each half of the expression seam is required: a renderer missing one cannot refuse anything.</summary>
    /// <param name="argument">Which constructor argument is missing.</param>
    [Theory]
    [InlineData("compiler")]
    [InlineData("predicates")]
    [InlineData("fields")]
    public void A_missing_expression_service_is_refused_at_construction(string argument)
        => Should.Throw<ArgumentNullException>(() => new ComputedColumnSql(
            argument == "compiler" ? null! : Compiler,
            argument == "predicates" ? null! : Predicates,
            argument == "fields" ? null! : new TestFieldSqlRenderer()));

    [Fact]
    public void A_null_entity_is_refused()
        => Should.Throw<ArgumentNullException>(() => Sut().For(null!, Total(Orders("unit_price * amount"))));

    [Fact]
    public void A_null_field_is_refused()
        => Should.Throw<ArgumentNullException>(() => Sut().For(Orders("unit_price * amount"), null!));

    /// <summary>A field that declares no <c>computed</c> is not a generated column and renders nothing.</summary>
    [Fact]
    public void A_field_without_a_computed_expression_renders_no_sql()
    {
        var entity = Orders(computed: null);

        Sut().For(entity, Total(entity)).ShouldBeNull();
    }

    /// <summary>
    /// Arithmetic over this entity's own fields renders to delimited identifiers and an operator — the shape
    /// that makes the text safe to reach DDL unparameterized.
    /// </summary>
    [Fact]
    public void Field_only_arithmetic_renders_to_delimited_identifiers()
    {
        var entity = Orders("unit_price * amount");

        Sut().For(entity, Total(entity)).ShouldBe("(\"unit_price\" * \"amount\")");
    }

    /// <summary>
    /// The descriptor mapper does not compile <c>computed</c>, so this is the first and only place an
    /// unresolvable one is caught — it must refuse rather than fall through to a column of its own.
    /// </summary>
    [Fact]
    public void A_computed_that_does_not_compile_is_refused()
    {
        var entity = Orders("unit_price * no_such_field");

        Should.Throw<InvalidOperationException>(() => Sut().For(entity, Total(entity)));
    }

    /// <summary>
    /// The refusal names the field it is about: the compiler's own errors are anchored to the expression,
    /// and an author fixing a descriptor needs to know which slot of it to open.
    /// </summary>
    [Fact]
    public void The_refusal_of_an_uncompilable_computed_names_the_field()
    {
        var entity = Orders("unit_price * no_such_field");

        var refusal = Should.Throw<InvalidOperationException>(() => Sut().For(entity, Total(entity)));

        refusal.Message.ShouldContain("orders.total");
    }

    /// <summary>
    /// Every problem is reported at once rather than one per round trip (§0 principle 4) — an agent fixing
    /// one field wants the whole list.
    /// </summary>
    [Fact]
    public void The_refusal_reports_every_compilation_error_not_only_the_first()
    {
        var refusal = RefuseWith(
            new CelCompilationError("first problem", null, 0),
            new CelCompilationError("second problem", null, 7));

        refusal.Message.ShouldContain("first problem");
        refusal.Message.ShouldContain("second problem");
    }

    /// <summary>
    /// A compiler error's fix suggestion is carried into the refusal: it is the half of a structured error
    /// an agent can act on, and dropping it would leave only the diagnosis.
    /// </summary>
    [Fact]
    public void The_refusal_carries_each_errors_fix_suggestion()
    {
        var refusal = RefuseWith(new CelCompilationError("unknown field", "did you mean net_total?", 0));

        refusal.Message.ShouldContain("did you mean net_total?");
    }

    /// <summary>
    /// A literal leaves the scalar renderer as a bind parameter, and a column definition is DDL, which has
    /// no bind-parameter form — so the field is refused rather than having the constant inlined into
    /// persisted schema text (spike Q9).
    /// </summary>
    [Fact]
    public void A_computed_carrying_a_constant_is_refused()
    {
        var entity = Orders("unit_price * 1.2");

        Should.Throw<InvalidOperationException>(() => Sut().For(entity, Total(entity)));
    }

    /// <summary>The refusal echoes the expression its author wrote, so the fix is visible without the descriptor.</summary>
    [Fact]
    public void The_constant_refusal_echoes_the_authored_expression()
    {
        var entity = Orders("unit_price * 1.2");

        var refusal = Should.Throw<InvalidOperationException>(() => Sut().For(entity, Total(entity)));

        refusal.Message.ShouldContain("orders.total");
        refusal.Message.ShouldContain("unit_price * 1.2");
    }

    /// <summary>
    /// It names the offending value, not the parameter marker the render produced: <c>@alvo_p0</c> tells an
    /// author nothing. The quoted form is what distinguishes the named constant from the echo of the source
    /// above, which carries the same digits as the author typed them.
    /// </summary>
    [Fact]
    public void The_constant_refusal_names_the_constant_value()
    {
        var entity = Orders("unit_price * 1.2");

        var refusal = Should.Throw<InvalidOperationException>(() => Sut().For(entity, Total(entity)));

        refusal.Message.ShouldContain("'1.2'");
    }

    /// <inheritdoc />
    public void Dispose() => _services.Dispose();

    private ICelCompiler Compiler => _services.GetRequiredService<ICelCompiler>();

    private IPredicateRenderer Predicates => _services.GetRequiredService<IPredicateRenderer>();

    private ComputedColumnSql Sut() => new(Compiler, Predicates, new TestFieldSqlRenderer());

    private InvalidOperationException RefuseWith(params CelCompilationError[] errors)
    {
        var entity = Orders("unit_price * amount");
        var sut = new ComputedColumnSql(new StubCompiler(errors), Predicates, new TestFieldSqlRenderer());

        return Should.Throw<InvalidOperationException>(() => sut.For(entity, Total(entity)));
    }

    private static EntitySchema Orders(string? computed) => new()
    {
        Name = "orders",
        Fields = [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "unit_price", Type = FieldType.Decimal, Required = true },
            new FieldSchema { Name = "amount", Type = FieldType.Integer, Required = true },
            new FieldSchema { Name = "total", Type = FieldType.Decimal, ComputedExpression = computed },
        ],
    };

    private static FieldSchema Total(EntitySchema entity) => entity.Fields.Single(f => f.Name == "total");

    /// <summary>A compiler that fails with exactly the errors a test wants to see reported.</summary>
    /// <param name="errors">The problems the compilation reports.</param>
    private sealed class StubCompiler(CelCompilationError[] errors) : ICelCompiler
    {
        public CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity)
            => CelCompilationResult.Failure(errors);
    }
}
