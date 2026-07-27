using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Abstractions.Tests.Schema;

/// <summary>
/// The one authority for which columns the framework owns, and which of them a caller may supply. It is
/// read by the descriptor mapper in the core and by every driver's write guard, neither of which can see the
/// other — so what it answers is the whole of what keeps those two in step.
/// </summary>
public class AlvoManagedColumnsTests
{
    [Fact]
    public void An_untenanted_plain_entity_manages_only_the_row_key()
        => AlvoManagedColumns.For(tenancy: null, audit: false, softDelete: false)
            .ShouldBe(["id"], ignoreOrder: true);

    [Fact]
    public void A_global_entity_manages_no_tenant_discriminator()
        => AlvoManagedColumns.For(TenancyMode.Global, audit: false, softDelete: false)
            .ShouldBe(["id"], ignoreOrder: true);

    [Fact]
    public void A_scoped_entity_manages_the_tenant_discriminator()
        => AlvoManagedColumns.For(TenancyMode.Scoped, audit: false, softDelete: false)
            .ShouldBe(["id", "tenant_id"], ignoreOrder: true);

    [Fact]
    public void An_audited_entity_manages_the_whole_quartet()
        => AlvoManagedColumns.For(TenancyMode.Global, audit: true, softDelete: false)
            .ShouldBe(["id", "created_at", "created_by", "updated_at", "updated_by"], ignoreOrder: true);

    /// <summary>
    /// Asserted on the authority itself, because <c>softDelete</c> is refused by the descriptor mapper until
    /// soft delete is implemented — so this is the one place the <c>deleted_at</c> answer stays pinned while
    /// the flag is unreachable through a descriptor.
    /// </summary>
    [Fact]
    public void A_soft_delete_entity_manages_the_deletion_stamp()
        => AlvoManagedColumns.For(TenancyMode.Global, audit: false, softDelete: true)
            .ShouldBe(["id", "deleted_at"], ignoreOrder: true);

    [Fact]
    public void An_entity_schema_answers_the_same_as_its_traits()
    {
        var entity = new EntitySchema
        {
            Name = "notes",
            Tenancy = TenancyMode.Scoped,
            Audit = true,
            Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
        };

        AlvoManagedColumns.For(entity).ShouldBe(
            AlvoManagedColumns.For(TenancyMode.Scoped, audit: true, softDelete: false), ignoreOrder: true);
    }

    /// <summary>
    /// The one asymmetry, and the whole reason this question is asked of the authority rather than of a name
    /// list: a create legitimately places a row in a tenant, and the tenant scope over the candidate row is
    /// what decides whether it may.
    /// </summary>
    [Fact]
    public void Only_the_tenant_discriminator_is_caller_writable_and_only_on_create()
    {
        AlvoManagedColumns.IsCallerWritable("tenant_id", isUpdate: false).ShouldBeTrue();
        AlvoManagedColumns.IsCallerWritable("tenant_id", isUpdate: true).ShouldBeFalse();

        foreach (var column in new[] { "id", "created_at", "created_by", "updated_at", "updated_by", "deleted_at" })
        {
            AlvoManagedColumns.IsCallerWritable(column, isUpdate: false).ShouldBeFalse();
            AlvoManagedColumns.IsCallerWritable(column, isUpdate: true).ShouldBeFalse();
        }
    }

    /// <summary>
    /// The refusal wording is here so both shipped implementations of the port state one refusal one way;
    /// the row key's reason differs per path, which is what the two shipped messages already said.
    /// </summary>
    [Theory]
    [InlineData("id", false, "cannot be supplied on create")]
    [InlineData("id", true, "can never be rewritten")]
    [InlineData("tenant_id", true, "never move to another tenant")]
    [InlineData("created_by", false, "managed by the framework")]
    [InlineData("updated_at", true, "managed by the framework")]
    public void The_refusal_reason_names_why_the_column_is_the_frameworks(string column, bool isUpdate, string expected)
        => AlvoManagedColumns.RefusalReason(column, isUpdate).ShouldContain(expected);

    [Fact]
    public void The_audit_quartet_is_the_four_columns_an_audit_entity_carries()
        => AlvoManagedColumns.Audit.ShouldBe(["created_at", "created_by", "updated_at", "updated_by"]);
}
