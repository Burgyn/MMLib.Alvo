namespace MMLib.Alvo.Identity.Probes;

/// <summary>
/// A type in the identity package's namespace, which this suite does not reference: the in-transaction ban matches
/// <c>MMLib.Alvo.Identity.*</c> by prefix (Ruling 54), and <c>HookDependenciesTests</c> pins that prefix with it.
/// </summary>
public interface IIdentityProbe;
