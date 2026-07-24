using System.ComponentModel.DataAnnotations;

namespace MMLib.Alvo;

/// <summary>Configuration options for Alvo.</summary>
public sealed class AlvoOptions
{
    /// <summary>Gets or sets the deployment mode.</summary>
    public AlvoMode Mode { get; set; } = AlvoMode.Standalone;

    /// <summary>Gets or sets the schema prefix for database objects.</summary>
    [RegularExpression("^[a-z][a-z0-9_]{0,15}$", ErrorMessage = "SchemaPrefix must be lower snake_case, 1–16 chars.")]
    public string SchemaPrefix { get; set; } = "alvo";
}
