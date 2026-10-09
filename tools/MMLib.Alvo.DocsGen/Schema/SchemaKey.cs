namespace MMLib.Alvo.DocsGen.Schema;

internal sealed record SchemaKey(
    string Path,
    string Anchor,
    string Type,
    bool Required,
    string Description,
    IReadOnlyList<string> Values,
    string? Default,
    string? Pattern,
    IReadOnlyList<string> Notes,
    bool IsConstant = false,
    string? RequiredDetail = null);
