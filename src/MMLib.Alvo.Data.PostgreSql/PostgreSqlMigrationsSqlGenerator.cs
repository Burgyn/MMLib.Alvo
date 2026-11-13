using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using MMLib.Alvo.Data.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Migrations;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// Spells PostgreSQL 15's <c>ON DELETE SET NULL (column)</c> for a tenant guard (#161): the guard's foreign key spans
/// <c>(tenant_id, ref)</c>, and a plain <c>SET NULL</c> would null <c>tenant_id</c> too — which is <c>NOT NULL</c> — so the
/// action must name the one column it clears.
/// </summary>
internal sealed class PostgreSqlMigrationsSqlGenerator : NpgsqlMigrationsSqlGenerator
{
#pragma warning disable EF1001 // INpgsqlSingletonOptions is EF-internal by design; the base constructor requires it, as CreateDatabaseModelFactory does.
    public PostgreSqlMigrationsSqlGenerator(MigrationsSqlGeneratorDependencies dependencies, INpgsqlSingletonOptions npgsqlSingletonOptions)
        : base(dependencies, npgsqlSingletonOptions)
    {
    }
#pragma warning restore EF1001

    /// <inheritdoc />
    protected override void ForeignKeyConstraint(AddForeignKeyOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        if (operation.OnDelete != ReferentialAction.SetNull || !TenantGuards.IsGuard(operation) || TenantGuards.GuardedField(operation.Columns) is not { } field)
        {
            base.ForeignKeyConstraint(operation, model, builder);
            return;
        }

        base.ForeignKeyConstraint(WithoutAction(operation), model, builder);
        builder.Append(" ON DELETE SET NULL (").Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(field)).Append(")");
    }

    private static AddForeignKeyOperation WithoutAction(AddForeignKeyOperation operation) => new()
    {
        Name = operation.Name,
        Schema = operation.Schema,
        Table = operation.Table,
        Columns = operation.Columns,
        PrincipalSchema = operation.PrincipalSchema,
        PrincipalTable = operation.PrincipalTable,
        PrincipalColumns = operation.PrincipalColumns,
        OnUpdate = operation.OnUpdate,
        OnDelete = ReferentialAction.NoAction,
    };
}
