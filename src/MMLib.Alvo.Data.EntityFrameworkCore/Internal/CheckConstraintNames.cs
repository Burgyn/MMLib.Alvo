using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Schema;
using System.Security.Cryptography;
using System.Text;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The one name a field's CHECK constraint has — a pure function of entity and field, so it never changes between applies —
/// computed and never parsed, and always hashed so an underscore shared between the two names cannot collide (HX-11 Ruling 19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> <c>ck_</c>, then <c>&lt;entity&gt;_&lt;field&gt;</c> cut to its first 51 characters, then <c>_</c> and the
/// first 8 lower-case hex digits of SHA-256 over <c>&lt;entity&gt;.&lt;field&gt;</c>. Identifiers are ASCII, so the cut is a
/// cut in bytes too: <c>3 + 51 + 1 + 8 = 63</c>, PostgreSQL's identifier limit.
/// </para>
/// <para>
/// <b>Found again by recomputing, never by reading the name back.</b> The readable part may be cut and may contain
/// underscores from either name, so a name cannot be split into its entity and field; <see cref="Owner"/> walks the
/// validated fields and recomputes each one's name instead.
/// </para>
/// </remarks>
internal static class CheckConstraintNames
{
    private const int MaxReadable = 51;
    private const int HashDigits = 8;

    /// <summary>The CHECK constraint name of <paramref name="entity"/>'s field <paramref name="field"/>.</summary>
    /// <param name="entity">The declaring entity's name.</param>
    /// <param name="field">The validated field's name.</param>
    internal static string For(string entity, string field)
    {
        var readable = $"{entity}_{field}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{entity}.{field}")))[..HashDigits];
        return $"ck_{(readable.Length <= MaxReadable ? readable : readable[..MaxReadable])}_{hash}";
    }

    /// <summary>
    /// The validated field whose constraint is named <paramref name="constraintName"/>, or <see langword="null"/> — the
    /// written entity searched first, then the parents that roll it up, then every other entity.
    /// </summary>
    /// <param name="schema">The schema the constraint belongs to.</param>
    /// <param name="written">The entity a write touched, whose own fields are the likeliest owners.</param>
    /// <param name="constraintName">The name the engine reported.</param>
    internal static (EntitySchema Entity, FieldSchema Field)? Owner(SchemaModel schema, EntitySchema written, string constraintName) =>
        Candidates(schema, written).FirstOrDefault(pair => string.Equals(For(pair.Entity.Name, pair.Field.Name), constraintName, StringComparison.Ordinal))
            is { Entity: not null } found
            ? found
            : null;

    /// <summary>Refuses a schema in which two validated fields would share one constraint name.</summary>
    /// <param name="schema">The schema a model is about to be built from.</param>
    /// <exception cref="DescriptorValidationException">Two validated fields share a name, reported at the second one's validation.</exception>
    internal static void EnsureDistinct(SchemaModel schema) => EnsureDistinct(schema, For);

    /// <summary>The same refusal over a given naming function — the seam a test reaches a collision through.</summary>
    /// <param name="schema">The schema a model is about to be built from.</param>
    /// <param name="name">The name each validated field's constraint gets.</param>
    internal static void EnsureDistinct(SchemaModel schema, Func<string, string, string> name)
    {
        var seen = new Dictionary<string, (EntitySchema Entity, FieldSchema Field)>(StringComparer.Ordinal);
        foreach (var pair in Validated(schema.Entities))
        {
            var constraint = name(pair.Entity.Name, pair.Field.Name);
            if (seen.TryGetValue(constraint, out var first))
            {
                throw Collision(first, pair, constraint);
            }

            seen.Add(constraint, pair);
        }
    }

    private static IEnumerable<(EntitySchema Entity, FieldSchema Field)> Candidates(SchemaModel schema, EntitySchema written) =>
        Validated(schema.Entities.OrderBy(entity => Rank(entity, written)));

    private static IEnumerable<(EntitySchema Entity, FieldSchema Field)> Validated(IEnumerable<EntitySchema> entities) =>
        entities.SelectMany(entity => entity.Fields.Where(field => field.CheckExpression is not null).Select(field => (entity, field)));

    private static int Rank(EntitySchema entity, EntitySchema written) =>
        string.Equals(entity.Name, written.Name, StringComparison.Ordinal) ? 0
        : entity.Fields.Any(field => string.Equals(field.Rollup?.From, written.Name, StringComparison.Ordinal)) ? 1
        : 2;

    private static DescriptorValidationException Collision(
        (EntitySchema Entity, FieldSchema Field) first, (EntitySchema Entity, FieldSchema Field) second, string constraint) =>
        new(new DescriptorValidationResult([new(
            $"/entities/{second.Entity.Name}/fields/{second.Field.Name}/validation",
            $"Field '{second.Entity.Name}.{second.Field.Name}' declares a 'validation' whose CHECK constraint would be named "
            + $"'{constraint}', the name '{first.Entity.Name}.{first.Field.Name}'s validation already has.",
            "Rename one of the two fields.",
            DescriptorValidationSeverity.Error)]));
}
