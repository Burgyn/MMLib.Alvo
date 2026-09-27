using MMLib.Alvo.Admin.Components.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// Each Fields row's declaration, read from the working copy — the one place a staged field's <c>hidden</c>,
/// <c>readOnly</c> or refused facets exist — and, for a field the copy removed, from the applied revision.
/// </summary>
public class FieldDeclarationsTests
{
    private const string Applied = """
        {"entities":{"orders":{"fields":{
          "notes":{"type":"text","hidden":true},
          "gone":{"type":"text","readOnly":true}}}}}
        """;

    private const string Working = """
        {"entities":{"orders":{"fields":{
          "notes":{"type":"text","hidden":"@user.id == owner"}}}}}
        """;

    [Fact]
    public void A_field_the_copy_declares_is_read_from_the_copy()
        => Kind(FieldDeclarations.From(Working, Applied, "orders"), "notes", "hidden").ShouldBe(JsonValueKind.String);

    [Fact]
    public void A_field_the_copy_removed_is_read_from_the_applied_revision()
        => Kind(FieldDeclarations.From(Working, Applied, "orders"), "gone", "readOnly").ShouldBe(JsonValueKind.True);

    [Fact]
    public void A_field_neither_declares_has_no_declaration()
        => FieldDeclarations.From(Working, Applied, "orders").Of("absent").ShouldBeNull();

    [Fact]
    public void Nothing_is_read_when_no_screen_cascaded_the_declarations()
        => FieldDeclarations.None.Of("notes").ShouldBeNull();

    private static JsonValueKind Kind(FieldDeclarations declarations, string field, string key)
        => declarations.Of(field)!.Value.GetProperty(key).ValueKind;
}
