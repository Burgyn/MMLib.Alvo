using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// How <see cref="ReadProjection"/> refuses: which argument a null is reported as, and what the defect it
/// raises on a schema/read-model disagreement actually tells the person reading the log.
/// </summary>
/// <remarks>
/// Separate from <see cref="ReadProjectionTests"/>, which pins the composed <c>SELECT</c> list itself.
/// </remarks>
public class ReadProjectionRefusalTests
{
    /// <summary>
    /// The mask is reported as the mask. <see cref="QueryFieldGuard.EnsureMaskable"/> guards the same two
    /// arguments a few lines later, so with the mask's own guard gone a null mask would be reported as
    /// whichever later argument happened to be missing too — a wrong name on the one argument that is a
    /// security control.
    /// </summary>
    [Fact]
    public void A_null_mask_is_named_as_the_mask_rather_than_as_a_later_argument()
        => Should.Throw<ArgumentNullException>(
                () => ReadProjection.Compose(Accounts, null!, Fields(), Dialect, null!))
            .ParamName.ShouldBe("hiddenFields");

    /// <summary>
    /// The defect names the field the two disagree about. This exception is raised on a state that is
    /// unreachable by construction, so the only thing it is worth is the one name that says which field the
    /// applied schema and the read model disagree about — without it a reader has an unreachable branch and
    /// nothing to look at.
    /// </summary>
    [Fact]
    public void The_schema_read_model_disagreement_names_the_field_they_disagree_about()
        => Should.Throw<InvalidOperationException>(
                () => ReadProjection.Compose(
                    Accounts with { Fields = [.. Accounts.Fields, Ghost] },
                    Fields(),
                    Fields("ghost"),
                    Dialect,
                    ReadModelFixture.Rows(Accounts)))
            .Message.ShouldContain("ghost");

    private static FieldSchema Ghost { get; } = new() { Name = "ghost", Type = FieldType.String };

    private static IAlvoSqlDialect Dialect { get; } = new TestSqlDialect();

    private static HashSet<string> Fields(params string[] fields) => fields.ToHashSet(StringComparer.Ordinal);

    private static EntitySchema Accounts { get; } = new()
    {
        Name = "accounts",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "title", Type = FieldType.String, Nullable = true },
        ],
    };
}
