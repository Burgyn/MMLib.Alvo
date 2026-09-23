using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which fields an entity's descriptor hides, read without the resolved schema, which does not say.
/// </summary>
public class FieldMasksTests
{
    private const string Descriptor = """
        {
          "entities": {
            "parts": {
              "fields": {
                "name": { "type": "string" },
                "shown": { "type": "string", "hidden": false },
                "secret": { "type": "string", "hidden": true },
                "margin": { "type": "decimal", "hidden": "!('admin' in @user.roles)" }
              }
            }
          }
        }
        """;

    [Fact]
    public void Hidden_true_is_hidden_from_everyone_and_a_cel_mask_from_some()
    {
        var masks = DescriptorLens.Masks(Descriptor, "parts");

        masks.Always.ShouldBe(["secret"]);
        masks.Conditional.ShouldBe(["margin"]);
        masks.NeverReturned("margin").ShouldBeFalse();
        masks.MayBeHidden("margin").ShouldBeTrue();
        masks.MayBeHidden("shown").ShouldBeFalse();
    }

    [Theory]
    [InlineData("customers")]
    [InlineData("{ not json")]
    public void An_undeclared_entity_or_an_unreadable_descriptor_masks_nothing(string entityOrJson)
    {
        var masks = entityOrJson.StartsWith('{')
            ? DescriptorLens.Masks(entityOrJson, "parts")
            : DescriptorLens.Masks(Descriptor, entityOrJson);

        masks.ShouldBe(FieldMasks.None);
    }
}
