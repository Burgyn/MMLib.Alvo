using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;
using DescriptorValidationException = MMLib.Alvo.Descriptor.DescriptorValidationException;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="CheckConstraintSql"/> and the model builder's use of it: the constraint's shape (the declaring field's own
/// NULL passes, the rest is two-valued), its refusals at the validation's pointer, and the refusal of a validated field
/// when no expression services reached the builder (HX-11 Rulings 15, 16, 19).
/// </summary>
public sealed class CheckConstraintSqlTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    [Fact]
    public void A_validation_renders_its_field_s_null_or_the_two_valued_predicate()
    {
        var entity = Products("stock >= 0");

        Sut().For(entity, Stock(entity)).ShouldBe("(\"stock\" IS NULL OR COALESCE(\"stock\" >= 0, FALSE))");
    }

    [Fact]
    public void A_field_without_a_validation_renders_nothing()
    {
        var entity = Products(validation: null);

        Sut().For(entity, Stock(entity)).ShouldBeNull();
    }

    [Theory]
    [InlineData("missing >= 0")]
    [InlineData("'admin' in @user.roles")]
    public void A_validation_with_no_constraint_rendering_is_refused_at_its_pointer(string validation)
    {
        var entity = Products(validation);

        var refusal = Should.Throw<DescriptorValidationException>(() => Sut().For(entity, Stock(entity)));

        refusal.Result.Errors.ShouldAllBe(error => error.Path == "/entities/products/fields/stock/validation");
        refusal.Message.ShouldContain("products.stock");
    }

    [Fact]
    public void The_model_builder_declares_the_check_under_its_computed_name()
    {
        var model = DescriptorModelBuilder.Build(new SchemaModel([Products("stock >= 0")]), NewSqliteBuilder, checks: Sut());

        var check = model.FindEntityType("products")!.GetCheckConstraints().ShouldHaveSingleItem();
        check.ModelName.ShouldBe(CheckConstraintNames.For("products", "stock"));
        check.Sql.ShouldBe("(\"stock\" IS NULL OR COALESCE(\"stock\" >= 0, FALSE))");
    }

    [Fact]
    public void A_validated_field_without_expression_services_is_refused_naming_AddAlvo() =>
        Should.Throw<InvalidOperationException>(() => DescriptorModelBuilder.Build(new SchemaModel([Products("stock >= 0")]), NewSqliteBuilder))
            .Message.ShouldSatisfyAllConditions(message => message.ShouldContain("products.stock"), message => message.ShouldContain("AddAlvo()"));

    public void Dispose() => _services.Dispose();

    private CheckConstraintSql Sut() => new(
        _services.GetRequiredService<ICelCompiler>(), _services.GetRequiredService<IPredicateRenderer>(), new TestFieldSqlRenderer());

    private static ModelBuilder NewSqliteBuilder() =>
        new(Microsoft.EntityFrameworkCore.Metadata.Conventions.SqliteConventionSetBuilder.Build());

    private static EntitySchema Products(string? validation) => new()
    {
        Name = "products",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = validation },
        ],
    };

    private static FieldSchema Stock(EntitySchema entity) => entity.Fields[1];
}
