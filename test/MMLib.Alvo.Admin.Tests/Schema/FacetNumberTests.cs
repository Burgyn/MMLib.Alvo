using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// A field editor's number box is read by the editor, not by the browser: text it cannot read, or a number the frozen
/// schema's bounds refuse, is a refusal at the box rather than a default quietly staged in its place.
/// </summary>
public sealed class FacetNumberTests
{
    [Theory]
    [InlineData("1e")]
    [InlineData("--3")]
    [InlineData("0")]
    [InlineData("1.5")]
    [InlineData("-1")]
    [InlineData("-120")]
    [InlineData("2147483648")]
    public void A_max_length_that_is_not_a_whole_number_of_one_or_more_is_refused(string text)
    {
        var read = FacetNumber.MaxLength(text);

        read.Problem.ShouldNotBeNull();
        read.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("120", 120)]
    public void An_empty_max_length_is_no_limit_and_a_number_is_itself(string? text, int? expected)
    {
        var read = FacetNumber.MaxLength(text);

        read.Problem.ShouldBeNull();
        read.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("1e", false)]
    [InlineData("0", false)]
    [InlineData("39", false)]
    [InlineData("-1", false)]
    [InlineData("-38", false)]
    [InlineData(" 12 ", true)]
    [InlineData("1", true)]
    [InlineData("38", true)]
    public void A_precision_is_a_whole_number_from_1_to_38(string text, bool read)
        => (FacetNumber.Precision(text).Problem is null).ShouldBe(read);

    [Theory]
    [InlineData("", false)]
    [InlineData("-1", false)]
    [InlineData("x", false)]
    [InlineData("0", true)]
    [InlineData("4", true)]
    public void A_scale_is_a_whole_number_of_zero_or_more(string text, bool read)
        => (FacetNumber.Scale(text).Problem is null).ShouldBe(read);
}
