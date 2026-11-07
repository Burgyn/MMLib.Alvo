namespace MMLib.Alvo.Analyzers.Tests.Generator;

/// <summary>Descriptor texts the generator's tests read: the shipped help-desk example, and small ones built inline.</summary>
internal static class Descriptors
{
    internal static string HelpDesk { get; } = Example("help-desk/help-desk.alvo.json");

    internal static string Example(string relative) =>
        File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "examples", relative));

    internal static string With(string entities, string extra = "", string name = "help-desk") =>
        $"{{ \"apiVersion\": \"alvo.dev/v1\", \"name\": \"{name}\", {extra} \"entities\": {entities} }}";
}
