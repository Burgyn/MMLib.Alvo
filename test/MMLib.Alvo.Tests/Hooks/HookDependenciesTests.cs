using Microsoft.Extensions.Logging;
using MMLib.Alvo.Ai;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Identity.Probes;
using MMLib.Alvo.Management;
using System.Data.Common;
using System.Net.Http;
using System.Net.Mail;
using System.Net.Sockets;
using System.Net.WebSockets;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>A stand-in for a host's mail sender: Alvo references no mail package, so the ban matches the name.</summary>
public interface IEmailSender;

/// <summary>A provider's connection type: every <see cref="DbConnection"/> subclass is banned, not only the base.</summary>
public abstract class ProbeConnection : DbConnection;

/// <summary>
/// The in-transaction dependency ban (Rulings 23, 54, 62), one row per banned family, so that deleting a family from
/// <see cref="HookDependencies"/> fails a row — and the types an in-transaction hook may take.
/// </summary>
public sealed class HookDependenciesTests
{
    public static TheoryData<Type> Banned { get; } =
    [
        typeof(HttpClient),
        typeof(IHttpClientFactory),
        typeof(Socket),
        typeof(ClientWebSocket),
        typeof(SmtpClient),
        Type.GetType("System.Net.WebRequest, System.Net.Requests", throwOnError: true)!,
        Type.GetType("System.Net.Quic.QuicConnection, System.Net.Quic", throwOnError: true)!,
        typeof(ProbeConnection),
        typeof(DbDataSource),
        typeof(Microsoft.AspNetCore.Identity.UserManager<object>),
        typeof(IIdentityProbe),
        typeof(IAlvoUserStore),
        typeof(IAlvoUserAdministration),
        typeof(IAlvoBootstrapAdmin),
        typeof(IEmailSender),
        typeof(MMLib.Alvo.Events.IEmailSender),
        typeof(IAlvoData),
        typeof(IAlvoSessions),
        typeof(IAlvoTransactions),
        typeof(IAlvoEvents),
        typeof(IOutboxStore),
        typeof(IAlvoManagement),
        typeof(IAlvoOperations),
        typeof(IDeliveryStore),
        typeof(IAlvoCommands),
        typeof(IAlvoEventPump),
        typeof(IAlvoAssistant),
        typeof(HttpClient[]),
        typeof(Func<IAlvoData>),
        typeof(Lazy<IAlvoSessions>),
    ];

    public static TheoryData<Type> Allowed { get; } =
    [
        typeof(ILogger<HookDependenciesTests>),
        typeof(TimeProvider),
        typeof(string[]),
        typeof(Func<string>),
    ];

    [Theory]
    [MemberData(nameof(Banned))]
    public void An_in_transaction_hook_may_not_depend_on(Type type) => HookDependencies.IsBanned(type).ShouldBeTrue();

    [Theory]
    [MemberData(nameof(Allowed))]
    public void An_in_transaction_hook_may_depend_on(Type type) => HookDependencies.IsBanned(type).ShouldBeFalse();
}
