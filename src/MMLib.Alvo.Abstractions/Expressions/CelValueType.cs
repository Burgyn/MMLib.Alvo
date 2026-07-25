namespace MMLib.Alvo.Expressions;

/// <summary>The runtime type of a CEL value inside an Alvo profile.</summary>
public enum CelValueType
{
    /// <summary>A boolean.</summary>
    Bool,

    /// <summary>A 64-bit signed integer.</summary>
#pragma warning disable CA1720
    Int,
#pragma warning restore CA1720

    /// <summary>An arbitrary-precision decimal number.</summary>
#pragma warning disable CA1720
    Decimal,
#pragma warning restore CA1720

    /// <summary>A UTF-8 string.</summary>
#pragma warning disable CA1720
    String,
#pragma warning restore CA1720

    /// <summary>An instant in time.</summary>
    Timestamp,

    /// <summary>A UUID.</summary>
    Uuid,

    /// <summary>Untyped JSON.</summary>
    Json,

    /// <summary>A list of strings, e.g. <c>@user.roles</c>.</summary>
    StringList,

    /// <summary>
    /// A placeholder type on every <see cref="CelFieldRef"/> the parser produces — only the
    /// type checker resolves a row field's real <see cref="CelValueType"/>.
    /// </summary>
    Null,
}
