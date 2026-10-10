namespace MMLib.Alvo.DocsGen.Configuration;

internal sealed record OutsideKey(string Key, string Type, string Default, string Description, string Source);

internal static class OutsideOptionsKeys
{
    internal static IReadOnlyList<OutsideKey> All { get; } =
    [
        new(
            "ConnectionStrings:Alvo",
            "string",
            "—",
            "The database connection string. The parameterless `UseSqlite()` and `UsePostgreSql()` read it; the standalone host reads it for "
            + "either driver and, for SQLite only, falls back to `Alvo:Database:SqliteConnectionString` when it is unset.",
            "src/MMLib.Alvo.Data.Sqlite/AlvoSqliteBuilderExtensions.cs"),
        new(
            "Alvo:Admin:CredentialAttemptsPerMinute",
            "int",
            "20",
            "Standalone host: sign-in and set-password attempts per minute for one subject (an address, or a set-password token) from one client. "
            + "Must be positive while the dashboard is on.",
            "src/MMLib.Alvo.Host/Internal/AlvoAdminCredentialLimit.cs"),
        new(
            "Alvo:Admin:CredentialCeilingPerMinute",
            "int",
            "200",
            "Standalone host: the ceiling per client per minute, shared by both credential forms, over the per-subject budget. "
            + "Must be positive while the dashboard is on.",
            "src/MMLib.Alvo.Host/Internal/AlvoAdminCredentialLimit.cs"),
        new(
            "Alvo:Admin:SessionRevalidationSeconds",
            "int",
            "30",
            "A test seam, not a setting: how often an open dashboard tab's session is re-checked. It can only shorten the interval; "
            + "anything but a whole number from 1 to 30 is refused at start.",
            "src/MMLib.Alvo.Identity/Internal/AlvoSessionRevalidationOptions.cs"),
    ];

    internal static string FinalSegment(string key) => key[(key.LastIndexOf(':') + 1)..];
}
