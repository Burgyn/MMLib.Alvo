using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// Which equality fields an exclusion's <c>EXCLUDE USING gist (… WITH =)</c> can compare on this server, through
/// <c>btree_gist</c>'s default GiST operator classes (issue 477).
/// </summary>
/// <remarks>
/// <para>
/// <b>Measured, not recalled</b> (postgres:12 to 16-alpine, 7 November 2026): <c>text</c>, <c>bigint</c>, <c>numeric</c>,
/// <c>uuid</c>, <c>date</c> and <c>timestamptz</c> have a default class on every one; <c>boolean</c> gained one in btree_gist
/// 1.7, which ships with PostgreSQL 15. A descriptor <c>enum</c> is stored as <c>text</c>, so btree_gist's own enum support
/// (PostgreSQL 12) never matters, and a <c>string</c> with a <c>maxLength</c> (<c>varchar</c>) is compared by the
/// <c>text</c> class.
/// </para>
/// <para>
/// <b>Installed → the catalogue decides; not installed → the version decides.</b> An installed extension's classes are
/// read from <c>pg_opclass</c>, which also catches a server upgraded with <c>pg_upgrade</c> that kept an older extension
/// version (a version-15 server without the boolean class: the fix is <c>ALTER EXTENSION … UPDATE</c>). Before the plan's
/// <c>CREATE EXTENSION</c> there is no class to read, and what it will install is the version the server ships — so
/// <see cref="MinimumServerVersion"/>'s table answers instead.
/// </para>
/// </remarks>
internal static class BtreeGistComparison
{
    /// <summary>
    /// The probe's fifth column: the column types an Alvo field is stored as that no default GiST operator class compares,
    /// comma separated — empty when every one is compared, and nearly every one when btree_gist is not installed.
    /// </summary>
    internal const string UncomparableTypesColumn = """
        (SELECT COALESCE(string_agg(t, ','), '')
         FROM unnest(ARRAY['text', 'bigint', 'numeric', 'boolean', 'uuid', 'date', 'timestamp with time zone']) AS t
         WHERE NOT EXISTS (SELECT 1 FROM pg_opclass c JOIN pg_am a ON a.oid = c.opcmethod
                           WHERE a.amname = 'gist' AND c.opcdefault AND c.opcintype = t::regtype))
        """;

    private const int Boolean = 150000;

    /// <summary>The refusals for every field of <paramref name="fields"/> this server's btree_gist cannot compare.</summary>
    /// <param name="fields">The equality fields of the exclusions being added.</param>
    /// <param name="installed">Whether btree_gist is installed in the database.</param>
    /// <param name="serverVersion">The server's <c>server_version_num</c>.</param>
    /// <param name="uncomparable">The <see cref="UncomparableTypesColumn"/> answer.</param>
    internal static IEnumerable<ExclusionRefusal> Refusals(
        IEnumerable<ExclusionEqualityField> fields, bool installed, int serverVersion, string uncomparable)
    {
        var missing = uncomparable.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return from field in fields
               let type = ProbedType(field.Type)
               where type is not null && (installed ? missing.Contains(type) : serverVersion < MinimumServerVersion(field.Type))
               select Refusal(field, installed, serverVersion);
    }

    private static ExclusionRefusal Refusal(ExclusionEqualityField field, bool installed, int serverVersion)
    {
        var minimum = MinimumServerVersion(field.Type);
        var where = installed && serverVersion >= minimum
            ? "the btree_gist installed in this database cannot do"
            : $"btree_gist cannot do on this PostgreSQL {Major(serverVersion)} server";
        var fix = serverVersion >= minimum
            ? "Ask the database owner to run ALTER EXTENSION btree_gist UPDATE; in this database, then apply again."
            : $"Upgrade the server to PostgreSQL {Major(minimum)} or later, or remove '{field.Name}' from 'fields'.";
        return new ExclusionRefusal(
            field.Pointer,
            $"Excluding overlaps per '{field.Name}' compares a {Spelled(field.Type)} for equality, which {where}. Nothing was applied.",
            fix);
    }

    /// <summary>The column type a field of <paramref name="type"/> is stored as, as the probe names it.</summary>
    private static string? ProbedType(FieldType type) => type switch
    {
        FieldType.String or FieldType.Text or FieldType.Enum => "text",
        FieldType.Integer => "bigint",
        FieldType.Decimal => "numeric",
        FieldType.Boolean => "boolean",
        FieldType.Uuid or FieldType.Ref => "uuid",
        FieldType.Date => "date",
        FieldType.DateTime => "timestamp with time zone",
        _ => null,
    };

    /// <summary>The first <c>server_version_num</c> whose shipped btree_gist compares <paramref name="type"/>.</summary>
    private static int MinimumServerVersion(FieldType type) => type == FieldType.Boolean ? Boolean : 0;

    private static int Major(int serverVersion) => serverVersion / 10000;

    private static string Spelled(FieldType type) => type.ToString().ToLowerInvariant();
}
