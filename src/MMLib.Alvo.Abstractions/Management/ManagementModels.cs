namespace MMLib.Alvo.Management;

/// <summary>What this Alvo instance is.</summary>
/// <param name="Version">The informational version of the running <c>MMLib.Alvo</c> assembly.</param>
/// <param name="Mode">
/// <c>standalone</c> or <c>embedded</c> — the host's own label when it set one, otherwise the registered
/// <see cref="AlvoMode"/>, lower-cased.
/// </param>
/// <param name="DataProvider">
/// The registered <c>IAlvoData</c> implementation's type name, or <c>none</c> when no driver is registered.
/// <b>Not the database engine:</b> the core may not reference the adapter that knows one.
/// </param>
/// <param name="StartupMode">The <c>Alvo:Schema:Startup</c> mode this process booted under, lower-cased.</param>
public sealed record ManagementInfo(string Version, string Mode, string DataProvider, string StartupMode);
