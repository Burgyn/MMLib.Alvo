using MMLib.Alvo.Ai.Internal;

using System.Text.Json;

namespace MMLib.Alvo.Ai.Tests;

/// <summary>The tool's policy over a patch, checked before the engine runs.</summary>
public sealed class PatchAdmissionTests
{
    [Theory]
    [InlineData("""[{"op":"replace","path":"","value":{}}]""")]
    [InlineData("""[{"op":"remove","path":""}]""")]
    [InlineData("""[{"op":"add","path":"","value":{}}]""")]
    [InlineData("""[{"op":"add","path":"/x","value":1},{"op":"copy","from":"/x","path":""}]""")]
    public void A_mutating_operation_on_the_whole_document_is_refused(string operations)
    {
        var refused = PatchAdmission.Check(Ops(operations)).ShouldNotBeNull();

        refused.Code.ShouldBe(JsonPatchError.WholeDocumentReplace);
        refused.Pointer.ShouldBe(string.Empty);
    }

    [Fact]
    public void A_test_of_the_whole_document_is_admitted() =>
        PatchAdmission.Check(Ops("""[{"op":"test","path":"","value":{}}]""")).ShouldBeNull();

    [Fact]
    public void More_than_fifty_operations_are_refused()
    {
        var operations = "[" + string.Join(",", Enumerable.Repeat("""{"op":"test","path":"/a","value":1}""", 51)) + "]";

        PatchAdmission.Check(Ops(operations))!.Code.ShouldBe(JsonPatchError.PatchTooLarge);
    }

    [Fact]
    public void More_than_sixteen_kilobytes_of_values_are_refused()
    {
        var value = new string('x', PatchAdmission.MaximumValueBytes);

        PatchAdmission.Check(Ops($$"""[{"op":"add","path":"/a","value":"{{value}}"}]"""))!.Code
            .ShouldBe(JsonPatchError.PatchTooLarge);
    }

    [Fact]
    public void An_ordinary_field_addition_is_admitted() =>
        PatchAdmission.Check(Ops("""[{"op":"add","path":"/entities/bikes/fields/notes","value":{"type":"text"}}]"""))
            .ShouldBeNull();

    private static JsonElement Ops(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
