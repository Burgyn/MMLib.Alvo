using MMLib.Alvo.Ai;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Data.Common;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What code inside a write's transaction may not depend on (spec 2026-10-14 §4.4.5, Rulings 23, 54, 62): the network, and every
/// port that would write beside the unit. Direct dependencies only, generic arguments and arrays included
/// (<c>Lazy&lt;IAlvoData&gt;</c>, <c>HttpClient[]</c>).
/// </summary>
/// <remarks>
/// What a direct check cannot see is the HX-10 analyzer's: a typed HTTP client whose own constructor takes the
/// <c>HttpClient</c>, a service resolved later from an <c>IServiceProvider</c>, and whatever a delegate hook's closure captured.
/// </remarks>
internal static class HookDependencies
{
    private static readonly Type[] _banned =
    [
        typeof(DbConnection), typeof(DbDataSource), typeof(IAlvoEvents), typeof(IAlvoData), typeof(IAlvoSessions),
        typeof(IAlvoTransactions), typeof(IOutboxStore), typeof(IDeliveryStore), typeof(IAlvoCommands), typeof(IAlvoEventPump),
        typeof(Management.IAlvoManagement), typeof(Management.IAlvoOperations), typeof(IAlvoUserStore),
        typeof(IAlvoUserAdministration), typeof(IAlvoBootstrapAdmin), typeof(IAlvoAssistant),
    ];

    private static readonly string[] _networkNamespaces =
        ["System.Net.Sockets", "System.Net.Http", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic"];

    /// <summary>Matched by name: naming the obsolete types would raise SYSLIB0014 in the Release build.</summary>
    private static readonly string[] _webRequestTypes = ["WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest"];

    /// <summary>Gets the types refused by name, for the analyzer's parity fact.</summary>
    internal static IReadOnlyList<Type> Banned => _banned;

    /// <summary>Gets the namespaces every type of which is refused.</summary>
    internal static IReadOnlyList<string> NetworkNamespaces => _networkNamespaces;

    /// <summary>Gets the <c>System.Net</c> type names refused by name.</summary>
    internal static IReadOnlyList<string> WebRequestTypes => _webRequestTypes;

    /// <summary>Gets the namespace prefixes of the identity stores.</summary>
    internal static IReadOnlyList<string> IdentityPrefixes { get; } = ["Microsoft.AspNetCore.Identity", "MMLib.Alvo.Identity"];

    internal static bool IsBanned(Type type) =>
        _networkNamespaces.Contains(type.Namespace)
        || (type.Namespace == "System.Net" && _webRequestTypes.Contains(type.Name))
        || IdentityPrefixes.Any(prefix => type.Namespace?.StartsWith(prefix, StringComparison.Ordinal) == true)
        || _banned.Any(banned => banned.IsAssignableFrom(type))
        || IsMailSender(type)
        || (type.IsGenericType && type.GetGenericArguments().Any(IsBanned))
        || (type.IsArray && IsBanned(type.GetElementType()!));

    private static bool IsMailSender(Type type) =>
        type.Name == "IEmailSender" || (type.IsGenericType && type.GetGenericTypeDefinition().Name == "IEmailSender`1");
}
