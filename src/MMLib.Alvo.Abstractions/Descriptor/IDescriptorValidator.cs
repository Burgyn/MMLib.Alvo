namespace MMLib.Alvo.Descriptor;

/// <summary>Validates a project descriptor before it is parsed and applied — the untrusted-input guardrail for the runtime path.</summary>
public interface IDescriptorValidator
{
    /// <summary>Validates descriptor JSON against the schema and Alvo's semantic rules.</summary>
    /// <param name="descriptorJson">The raw descriptor JSON.</param>
    /// <returns>All findings; check <see cref="DescriptorValidationResult.IsValid"/>.</returns>
    DescriptorValidationResult Validate(string descriptorJson);
}
