namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// The <c>access</c> block's three level keys, validated against the schema from a sample this suite owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this sample is here rather than under <c>examples/</c>.</b> The type-2 corpus <em>is</em> the
/// examples tree, and <c>complex-crm</c> was the one fixture that declared all three levels — with
/// <c>admin</c> and <c>developer</c> byte-identical, which under highest-match-wins is a level no caller
/// can ever hold. That level was removed rather than kept for a schema-key tick, and no example can
/// honestly declare a <c>developer</c> one: none of them has a role that means it, and inventing a role
/// would assert an org shape the example never had.
/// </para>
/// <para>
/// <b>The key still has to stay covered, because nothing else covers it.</b> The compiler's own facts
/// (<c>AccessCatalogBuilderTests</c>) exercise <c>developer</c> from a hand-built <c>Access</c> object,
/// which never meets the schema — so without this, a <c>developer</c> subschema broken in
/// <c>project.schema.json</c> would be caught by no fact anywhere.
/// </para>
/// </remarks>
public class AccessLevelsTests
{
    /// <summary>
    /// A minimal descriptor declaring all three levels as three <b>distinct</b> predicates — distinct
    /// because identical ones are what this sample exists to stop being written into an example.
    /// </summary>
    private const string AllThreeLevels = """
        {
          "$schema": "https://alvo.dev/schema/v1/project.json",
          "apiVersion": "alvo.dev/v1",
          "name": "access-levels",
          "description": "The three management-access levels, one distinct predicate each.",
          "auth": {
            "providers": ["local"],
            "roles": ["owner", "engineer", "auditor"]
          },
          "access": {
            "admin": "'owner' in @user.roles",
            "developer": "'engineer' in @user.roles",
            "viewer": "'auditor' in @user.roles"
          },
          "entities": {
            "notes": {
              "description": "One note, so the descriptor declares an entity.",
              "fields": {
                "title": { "type": "string", "required": true, "maxLength": 200 }
              },
              "rules": {
                "list": "'auditor' in @user.roles"
              }
            }
          }
        }
        """;

    /// <summary>
    /// <b>All three <c>access</c> level keys validate.</b>
    /// </summary>
    [Fact]
    public void A_descriptor_declaring_all_three_levels_validates()
    {
        var failures = SchemaValidator.Failures(SchemaValidator.Load(), AllThreeLevels);

        failures.ShouldBeEmpty(
            "admin, developer and viewer are the three keys the frozen schema declares under 'access', and "
            + "a key no positive sample reaches is a key a broken subschema can silently stop accepting");
    }
}
