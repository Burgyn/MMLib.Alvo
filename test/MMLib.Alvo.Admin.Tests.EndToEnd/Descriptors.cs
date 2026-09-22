using System.Reflection;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// The descriptor this suite boots.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from the repository, not copied here.</b> <c>examples/field-service</c> is the example
/// whose whole reason to exist is that every construct in it demonstrates one behaviour the
/// framework claims — so a suite that drove a descriptor written for itself would be proving the
/// dashboard against a world nothing else uses. A copy would drift the first time the example
/// gained a field.
/// </para>
/// </remarks>
internal static class Descriptors
{
    /// <summary>The field-service example, exactly as it sits in the repository.</summary>
    public static string FieldService { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "field-service", "field-service.alvo.json"));
}
