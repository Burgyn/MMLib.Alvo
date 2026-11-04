using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using System.Data.Common;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What code inside a write's transaction may not depend on (spec 2026-10-14 §4.4.5, Ruling 23): the network, and every data
/// port that would write beside the unit. Direct dependencies only, generic arguments included (<c>Lazy&lt;IAlvoData&gt;</c>).
/// </summary>
internal static class HookDependencies
{
    private static readonly Type[] _banned =
    [
        typeof(DbConnection), typeof(IAlvoEvents), typeof(IAlvoData), typeof(IAlvoSessions), typeof(IAlvoTransactions),
        typeof(IOutboxStore), typeof(IDeliveryStore), typeof(IAlvoCommands), typeof(Management.IAlvoManagement),
        typeof(Management.IAlvoOperations),
    ];

    private static readonly string[] _networkNamespaces =
        ["System.Net.Sockets", "System.Net.Http", "System.Net.WebSockets", "System.Net.Mail", "System.Net.Quic"];

    /// <summary>Matched by name: naming the obsolete types would raise SYSLIB0014 in the Release build.</summary>
    private static readonly string[] _webRequestTypes = ["WebClient", "WebRequest", "HttpWebRequest", "FtpWebRequest"];

    internal static bool IsBanned(Type type) =>
        _networkNamespaces.Contains(type.Namespace)
        || (type.Namespace == "System.Net" && _webRequestTypes.Contains(type.Name))
        || type.Namespace?.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal) == true
        || type.Namespace?.StartsWith("MMLib.Alvo.Identity", StringComparison.Ordinal) == true
        || _banned.Any(banned => banned.IsAssignableFrom(type))
        || IsMailSender(type)
        || (type.IsGenericType && type.GetGenericArguments().Any(IsBanned))
        || (type.IsArray && IsBanned(type.GetElementType()!));

    private static bool IsMailSender(Type type) =>
        type.Name == "IEmailSender" || (type.IsGenericType && type.GetGenericTypeDefinition().Name == "IEmailSender`1");
}
