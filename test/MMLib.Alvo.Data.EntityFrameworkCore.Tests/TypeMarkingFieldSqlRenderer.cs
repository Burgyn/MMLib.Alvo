using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <c>TestFieldSqlRenderer</c> with one difference: its value repair is visible. Every operand pair a renderer
/// routes through <see cref="IFieldSqlRenderer.RenderComparableOperands"/> comes back marked with the type it
/// was compared at, which is how a test asserts <em>which</em> comparisons are repaired at all — the shipped
/// renderers' repair is a no-op for every type but <c>decimal</c>, and SQLite's is invisible here.
/// </summary>
internal sealed class TypeMarkingFieldSqlRenderer : IFieldSqlRenderer
{
    private readonly TestFieldSqlRenderer _inner = new();

    public string TrueLiteral => _inner.TrueLiteral;

    public string FalseLiteral => _inner.FalseLiteral;

    public string RenderField(EntitySchema entity, string fieldName) => _inner.RenderField(entity, fieldName);

    public string RenderParameter(string parameterName) => _inner.RenderParameter(parameterName);

    public string RenderCaseInsensitiveLike(string left, string right) => _inner.RenderCaseInsensitiveLike(left, right);

    public (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type) =>
        ($"CMP<{type}>({left})", $"CMP<{type}>({right})");
}
