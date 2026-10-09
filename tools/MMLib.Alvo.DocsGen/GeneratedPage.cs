namespace MMLib.Alvo.DocsGen;

internal enum OutputRoot
{
    Reference,
    Docs,
    Generated,
    Public,
}

internal sealed record GeneratedPage(OutputRoot Root, string RelativePath, string Content);
