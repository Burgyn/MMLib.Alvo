namespace MMLib.Alvo.Secrets.Internal;

/// <summary>A secret this host declared with <c>AddRequiredSecret</c>: it does not start without it.</summary>
/// <param name="Name">The secret's name in the store — never its value.</param>
internal sealed record RequiredSecret(SecretName Name);
