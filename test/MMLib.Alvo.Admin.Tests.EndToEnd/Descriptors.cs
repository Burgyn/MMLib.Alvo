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

    /// <summary>
    /// The complex-crm example, exactly as it sits in the repository — imported into a working copy, never
    /// booted: it is five ref layers deep and declares automation, which field-service has neither of.
    /// </summary>
    public static string ComplexCrm { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "complex-crm", "crm.alvo.json"));

    /// <summary>
    /// The bike-workshop example, exactly as it sits in the repository — the demo world <c>scripts/demo-admin</c>
    /// boots, and the one whose customers the maintainer asked a computed <c>full_name</c> of.
    /// </summary>
    public static string BikeWorkshop { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "bike-workshop", "bike-workshop.alvo.json"));

    /// <summary>
    /// The vehicle-registry example, exactly as it sits in the repository — the descriptor the embedded-host sample runs,
    /// whose <c>vehicles.vin</c> its README registers <c>normalizeVin</c> for.
    /// </summary>
    public static string VehicleRegistry { get; } = File.ReadAllText(
        Path.Combine(RepositoryRoot.Find(), "examples", "vehicle-registry", "vehicles.alvo.json"));
}
