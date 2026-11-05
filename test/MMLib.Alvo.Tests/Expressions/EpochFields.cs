using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Expressions;

/// <summary>A neutral dialect that renders the microsecond epoch as <c>EPOCH_US(x)</c>, so a golden reads the shape alone.</summary>
internal sealed class EpochFields : IFieldSqlRenderer
{
    public static EpochFields Instance { get; } = new();

    public string TrueLiteral => "TRUE";

    public string FalseLiteral => "FALSE";

    public string RenderField(EntitySchema entity, string fieldName) => $"\"{fieldName}\"";

    public string RenderParameter(string parameterName) => "@" + parameterName;

    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} LIKE {right}";

    public string? RenderEpochMicroseconds(string timestampSql) => $"EPOCH_US({timestampSql})";
}

/// <summary><see cref="EpochFields"/> without the epoch: a dialect that inherits the port's refusal.</summary>
internal sealed class NoEpochFields : IFieldSqlRenderer
{
    public static NoEpochFields Instance { get; } = new();

    public string TrueLiteral => "TRUE";

    public string FalseLiteral => "FALSE";

    public string RenderField(EntitySchema entity, string fieldName) => $"\"{fieldName}\"";

    public string RenderParameter(string parameterName) => "@" + parameterName;

    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} LIKE {right}";
}
