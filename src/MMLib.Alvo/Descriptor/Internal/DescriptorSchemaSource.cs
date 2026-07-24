using System.Reflection;

namespace MMLib.Alvo.Descriptor.Internal;

/// <summary>Reads the embedded <c>project.schema.json</c> once, so the validator needs no filesystem access.</summary>
internal static class DescriptorSchemaSource
{
    private const string ResourceName = "MMLib.Alvo.project.schema.json";

    /// <summary>Gets the embedded schema JSON text.</summary>
    public static string Json { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(DescriptorSchemaSource).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
