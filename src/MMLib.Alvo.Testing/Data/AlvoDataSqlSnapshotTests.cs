using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using Xunit;
using static VerifyXunit.Verifier;

namespace MMLib.Alvo.Testing.Data;

/// <summary>
/// The per-engine half of the golden CEL→SQL snapshots: the same rule table the core freezes against
/// <c>TestFieldSqlRenderer</c>, re-rendered through a real driver's <see cref="IFieldSqlRenderer"/>, so
/// a dialect change (a boolean literal, an <c>ILIKE</c> spelling, a quoting rule) shows up as a moved
/// baseline rather than as a behaviour difference discovered on one engine only.
/// </summary>
/// <remarks>
/// The compiler, renderer and field renderer arrive as abstract members because this library
/// deliberately references <c>MMLib.Alvo.Abstractions</c> alone — a subclass in an engine's own test
/// project resolves them from <c>AddAlvo()</c> and its driver package.
/// </remarks>
public abstract class AlvoDataSqlSnapshotTests
{
    private static readonly string[] _rules =
    [
        "true",
        "owner_id == @user.id",
        "owner_id != @user.id",
        "!(owner_id == @user.id)",
        "tenant_id == @tenant.id",
        "has(owner_id)",
        "!has(owner_id)",
        "is_public",
        "is_public || owner_id == @user.id",
        "'admin' in @user.roles",
        "status == 'approved'",
        "status in @user.roles",
        "(owner_id == @user.id || status == 'approved') && !is_public",
    ];

    /// <summary>Gets the engine's snapshot file suffix (<c>sqlite</c>, <c>postgresql</c>).</summary>
    protected abstract string EngineName { get; }

    /// <summary>Gets the CEL compiler, resolved from <c>AddAlvo()</c>.</summary>
    protected abstract ICelCompiler Compiler { get; }

    /// <summary>Gets the predicate renderer, resolved from <c>AddAlvo()</c>.</summary>
    protected abstract IPredicateRenderer Renderer { get; }

    /// <summary>Gets the driver's own field/dialect renderer.</summary>
    protected abstract IFieldSqlRenderer Fields { get; }

    /// <summary>
    /// Gets the rules this engine's snapshot renders. Overridable so a dialect whose boolean handling
    /// genuinely differs — T-SQL is the named case, and the only reason
    /// <see cref="IFieldSqlRenderer"/>'s three two-valued members ship as default interface members —
    /// can append its own cases without editing this shipped base class. An override should keep the
    /// inherited rules and add to them, since a subclass that replaced them would stop comparing like
    /// with like against the other engines.
    /// </summary>
    protected virtual IEnumerable<string> Rules => _rules;

    /// <summary>
    /// The caller every snapshot renders against — a fixed, tenanted, admin-holding identity.
    /// </summary>
    /// <remarks>
    /// Public, not <see langword="protected"/>, together with <see cref="SnapshotEntity"/>: this pair is
    /// the framework's one canonical data-path fixture, and every engine's data-path test project reads it
    /// from classes that do not derive from this suite (a binder test, a statement-composer test, an
    /// adversarial fixture). A second, per-project copy of a fixture entity is how two test suites come to
    /// disagree about what they are testing over.
    /// </remarks>
    public static AlvoContext SnapshotCaller { get; } = new()
    {
        User = new UserId(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001")),
        Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        Tenant = new TenantId(Guid.Parse("11111111-0000-0000-0000-000000000001")),
    };

    /// <summary>
    /// The entity every snapshot rule is compiled against, and the shared fixture entity every data-path
    /// suite reads and writes — one column of every field type, one nullable owner reference, one field a
    /// <c>hidden</c> rule can mask. See <see cref="SnapshotCaller"/> for why the pair is public.
    /// </summary>
    public static EntitySchema SnapshotEntity { get; } = new()
    {
        Name = "vehicle",
        Tenancy = TenancyMode.Scoped,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "tenant_id", Type = FieldType.Uuid, Required = true, Indexed = true },
            new FieldSchema { Name = "owner_id", Type = FieldType.Uuid, Nullable = true },
            new FieldSchema { Name = "plate", Type = FieldType.String, Required = true, MaxLength = 32 },
            new FieldSchema { Name = "status", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "secret_note", Type = FieldType.String, Nullable = true },
            new FieldSchema { Name = "mileage", Type = FieldType.Integer, Nullable = true },
            new FieldSchema { Name = "price", Type = FieldType.Decimal, Nullable = true, Precision = 18, Scale = 2 },
            new FieldSchema { Name = "is_public", Type = FieldType.Boolean, Nullable = true },
            new FieldSchema { Name = "created_at", Type = FieldType.DateTime, Nullable = true },
        ],
    };

    /// <summary>
    /// Freezes the SQL this engine's dialect renders the fixed rule table into.
    /// </summary>
    /// <remarks>
    /// This suite's job is dialect <em>shape</em>, and the parameter list deliberately records each bound
    /// value's CLR type rather than the value: recording values would make the row for
    /// <c>status in @user.roles</c> depend on <c>HashSet&lt;Role&gt;</c> enumeration order. So it does not
    /// prove that the <em>right</em> context value was bound — a renderer swapping <c>@tenant.id</c> for
    /// <c>@user.id</c>, both <see cref="Guid"/>, would produce a byte-identical baseline. That property
    /// belongs to the core, where <c>cel-to-sql-core</c> records the values themselves and
    /// <c>SqlPredicateRendererTests</c> asserts the binding directly.
    /// </remarks>
    [Fact]
    public Task Cel_renders_to_this_engines_sql()
    {
        var rendered = Rules.Select(rule => Snapshot(rule, Render(rule))).ToList();

        return Verify(rendered).UseFileName($"cel-to-sql-{EngineName}");
    }

    private static object Snapshot(string rule, SqlPredicate predicate) => new
    {
        Rule = rule,
        predicate.Sql,
        Parameters = predicate.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}:{p.Value?.GetType().Name ?? "null"}")
            .ToArray(),
    };

    private SqlPredicate Render(string rule)
    {
        var compiled = Compiler.Compile(rule, CelProfile.Rule, SnapshotEntity);
        if (!compiled.IsSuccess)
        {
            throw new InvalidOperationException(
                $"'{rule}' did not compile: {string.Join("; ", compiled.Errors.Select(e => e.Message))}");
        }

        return Renderer.Render(compiled.Expression!, SnapshotCaller, Fields, "alvo_u");
    }
}
