namespace MMLib.Alvo.Migrations;

/// <summary>Port for loading project descriptors.</summary>
public interface IDescriptorSource
{
    /// <summary>Loads a project descriptor.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The descriptor JSON as a string.</returns>
    Task<string> LoadAsync(CancellationToken ct = default);
}
