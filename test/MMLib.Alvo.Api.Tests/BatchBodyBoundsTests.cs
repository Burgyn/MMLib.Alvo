using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using System.Text;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// The batch body's shape scan, on its own — the layer that has to refuse an over-long batch <b>before</b>
/// it is parsed into a node tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tested here rather than only over HTTP, because the HTTP fact cannot tell the two refusals apart.</b>
/// <c>BatchBodyReader</c> re-checks the row count over the parsed array as a belt, so a route test answers
/// 422 with the same code whether the scan caught it or the belt did — and the belt catches it only after
/// the whole body has been materialised, which is the cost the bound exists to refuse. Only a test of the
/// scan itself can say which one fired.
/// </para>
/// <para>
/// The gap this pins was real: the first scan counted an object, a string and a number — the shapes a valid
/// row can take — so <c>null</c>, <c>true</c>, <c>false</c> and a nested array were never counted, and a
/// body of two hundred thousand nulls scanned as zero rows.
/// </para>
/// </remarks>
public sealed class BatchBodyBoundsTests
{
    /// <summary>Every element counts toward the row bound, whatever its JSON type.</summary>
    /// <param name="element">The element to repeat.</param>
    [Theory]
    [InlineData("{\"a\":1}")]
    [InlineData("\"9f8b1c2d-0000-0000-0000-000000000000\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("[]")]
    public async Task Every_element_of_the_rows_array_counts_toward_the_bound(string element)
    {
        var options = new AlvoApiOptions { MaxBatchRows = 3 };

        var refusal = await ScanAsync($"{{\"rows\":[{string.Join(",", Enumerable.Repeat(element, 10))}]}}", options);

        refusal.ShouldBe(
            BodyRefusal.TooManyRows,
            $"ten '{element}' elements is past a bound of three, and the scan is what has to say so");
    }

    /// <summary>A batch inside the bound is not refused, so the fact above cannot pass by refusing everything.</summary>
    [Fact]
    public async Task A_batch_inside_the_bound_is_not_refused()
    {
        var options = new AlvoApiOptions { MaxBatchRows = 3 };

        (await ScanAsync("{\"rows\":[null,null,null]}", options)).ShouldBeNull();
    }

    /// <summary>
    /// The field bound is spent per row, so many small rows are admitted where one shared budget would refuse
    /// them.
    /// </summary>
    [Fact]
    public async Task The_field_bound_is_spent_per_row()
    {
        var options = new AlvoApiOptions { MaxPayloadKeys = 4, MaxBatchRows = 100 };
        var row = "{\"a\":1,\"b\":2,\"c\":3}";

        var refusal = await ScanAsync($"{{\"rows\":[{string.Join(",", Enumerable.Repeat(row, 20))}]}}", options);

        refusal.ShouldBeNull("three fields a row is inside a per-row bound of four");
    }

    /// <summary>And a single row past that bound is still refused, so the bound is scoped rather than absent.</summary>
    [Fact]
    public async Task A_row_past_the_field_bound_is_still_refused()
    {
        var options = new AlvoApiOptions { MaxPayloadKeys = 2, MaxBatchRows = 100 };

        (await ScanAsync("{\"rows\":[{\"a\":1,\"b\":2,\"c\":3}]}", options)).ShouldBe(BodyRefusal.TooManyKeys);
    }

    /// <summary>
    /// An array named <c>rows</c> <em>inside</em> a row does not arm the counter, so a nested one cannot make
    /// the scan lose track of the real one.
    /// </summary>
    [Fact]
    public async Task A_nested_rows_array_does_not_arm_the_counter()
    {
        var options = new AlvoApiOptions { MaxBatchRows = 2 };
        var nested = "{\"rows\":[1,2,3,4,5,6,7,8,9]}";

        var refusal = await ScanAsync($"{{\"rows\":[{nested},{nested},{nested}]}}", options);

        refusal.ShouldBe(BodyRefusal.TooManyRows, "three real rows past a bound of two — not nine nested values");
    }

    /// <summary>The bound applies only to the reserved member, so an ordinary body is unaffected.</summary>
    [Fact]
    public async Task An_ordinary_body_is_not_measured_in_rows()
    {
        var options = new AlvoApiOptions { MaxBatchRows = 2 };

        (await ScanAsync("{\"rows\":[1,2,3,4,5]}", options, rowsMember: null)).ShouldBeNull();
    }

    /// <summary>Runs the scan over <paramref name="json"/>.</summary>
    /// <param name="json">The body.</param>
    /// <param name="options">The bounds to enforce.</param>
    /// <param name="rowsMember">The reserved member, or <see langword="null"/> for an ordinary body.</param>
    private static async Task<BodyRefusal?> ScanAsync(
        string json, AlvoApiOptions options, string? rowsMember = BatchViolations.RowsMember)
    {
        var request = new DefaultHttpContext().Request;
        request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        request.ContentLength = request.Body.Length;

        using var destination = new MemoryStream();

        return await BoundedJsonBody.ReadAsync(
            request, destination, options, rowsMember, TestContext.Current.CancellationToken);
    }
}
