namespace MMLib.Alvo.Schema;

/// <summary>Enumeration of field types in an entity schema.</summary>
public enum FieldType
{
    /// <summary>String type.</summary>
#pragma warning disable CA1720
    String,
#pragma warning restore CA1720

    /// <summary>Text (long) type.</summary>
    Text,

    /// <summary>Integer type.</summary>
#pragma warning disable CA1720
    Integer,
#pragma warning restore CA1720

    /// <summary>Decimal type.</summary>
#pragma warning disable CA1720
    Decimal,
#pragma warning restore CA1720

    /// <summary>Boolean type.</summary>
    Boolean,

    /// <summary>Date type.</summary>
    Date,

    /// <summary>DateTime type.</summary>
    DateTime,

    /// <summary>UUID type.</summary>
    Uuid,

    /// <summary>JSON type.</summary>
    Json,

    /// <summary>Enum type.</summary>
    Enum,

    /// <summary>Reference/Foreign key type.</summary>
    Ref
}
