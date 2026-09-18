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

/// <summary>One project this instance serves.</summary>
/// <param name="Name">The project name — the descriptor's own <c>name</c>.</param>
/// <param name="Revision">The applied revision, or <see langword="null"/> when nothing has been applied yet.</param>
/// <param name="Phase">The boot phase, lower-cased: <c>pending</c>, <c>ready</c> or <c>failed</c>.</param>
public sealed record ManagementProject(string Name, int? Revision, string Phase);

/// <summary>A project's current descriptor and the revision an apply must echo.</summary>
/// <param name="Project">The project name.</param>
/// <param name="Revision">The current revision; <c>0</c> when nothing has been applied yet.</param>
/// <param name="DescriptorJson">
/// The descriptor exactly as it was applied. <b>Not a re-serialisation:</b> what a caller exports is the
/// stored text, which is the only shape under which "everything clickable is exportable as code" can be
/// true without drift.
/// </param>
public sealed record ManagementDescriptor(string Project, int Revision, string DescriptorJson);
