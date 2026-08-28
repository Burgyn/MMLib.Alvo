using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// A before-hook <b>cannot</b> make a network call. Not "should not", not "is reviewed for" — the
/// <c>alvo-security-core-review</c> checklist requires it to be inexpressible, and this file is where that is
/// measured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent halves, because either alone is escapable.</b> The port's signature closes the direct
/// route: <see cref="IBeforeHookRunner.Run"/> returns no task and takes no cancellation token, so nothing
/// inside it can await a socket without blocking a thread that is holding a write transaction's locks. This
/// file's other fact closes the indirect one: nothing reachable from the runner's constructor <em>holds</em> a
/// client either. A signature cannot say "and nothing you depend on may do it", and a dependency scan cannot
/// say "and you may not block on one".
/// </para>
/// <para>
/// <b>Asserted over the type's actual dependencies, never over a naming convention.</b> The walk starts at the
/// runner's constructor parameters, follows every instance field, expands generic arguments and array
/// elements, and — for an injected interface or abstract type — follows every implementation the shipped
/// assemblies offer, because that is what a container may hand it. A rule matching type <em>names</em> would
/// pass a dependency called <c>Notifier</c> that holds an <see cref="HttpClient"/>; this one does not, and
/// <see cref="The_scan_reports_a_client_two_hops_from_the_constructor"/> is the control that proves it.
/// </para>
/// <para>
/// <b>Why a before-hook may not do what an after-hook must.</b> A before-hook runs inside the write's
/// transaction, so a network call there is a row lock held for the duration of a stranger's timeout — and an
/// external side effect inside a transaction that may still roll back. After-hooks are driven from the outbox,
/// which by construction holds only committed events, so that is where a network call belongs. A hook that
/// needs one is a hook on the wrong rung.
/// </para>
/// </remarks>
public class BeforeHookIsolationArchitectureTests
{
    /// <summary>
    /// Every way this build can reach a network, by full type name. Matched by name rather than by
    /// <c>typeof</c> for two reasons: the mail port and the webhook delivery are declared in assemblies this
    /// walk treats as data rather than as references, and a name list is what
    /// <see cref="Every_forbidden_name_still_resolves_to_a_real_type"/> can prove is not silently stale.
    /// </summary>
    private static readonly string[] _forbidden =
    [
        "System.Net.Http.HttpClient",
        "System.Net.Http.IHttpClientFactory",

        // The base type of every HttpClient: a dependency typed as the base is the same reach by another name.
        "System.Net.Http.HttpMessageInvoker",
        "System.Net.Sockets.Socket",
        "MMLib.Alvo.Events.IEmailSender",
        "MMLib.Alvo.Events.Internal.WebhookDelivery",
    ];

    /// <summary>
    /// The fact. Nothing reachable from <c>BeforeHookRunner</c>'s constructor closure can reach a network.
    /// </summary>
    [Fact]
    public void Nothing_a_before_hook_can_reach_can_make_a_network_call()
    {
        var closure = Closure(typeof(BeforeHookRunner));

        closure.Count.ShouldBeGreaterThan(
            1, "the walk found only the runner itself, so an empty offender list proves nothing");

        var offenders = Offenders(closure);
        offenders.ShouldBeEmpty(
            "a before-hook runs inside the write's transaction, so a network call from anything it can reach "
            + "would hold a row lock for a stranger's timeout — and would be an external side effect inside a "
            + "transaction that may still roll back. Put it on an after-hook, which runs after the commit. "
            + $"Offenders: {string.Join(", ", offenders)}.");
    }

    /// <summary>
    /// The port cannot await anything, which is the same ban expressed where a reader meets it first.
    /// </summary>
    /// <remarks>
    /// A future <c>RunAsync</c> would make an HTTP call the most natural thing in the world to write inside a
    /// hook, and no dependency scan would notice — the client would arrive as a method argument or a captured
    /// local rather than as a constructor dependency.
    /// </remarks>
    [Fact]
    public void The_ports_own_signature_cannot_await_anything()
    {
        var run = typeof(IBeforeHookRunner).GetMethod(nameof(IBeforeHookRunner.Run))!;

        typeof(Task).IsAssignableFrom(run.ReturnType).ShouldBeFalse("a synchronous method cannot await a socket");
        run.ReturnType.ShouldNotBe(typeof(ValueTask));
        run.GetParameters().ShouldNotContain(
            parameter => parameter.ParameterType == typeof(CancellationToken),
            "a cancellation token is what an awaited call would be given, and there is nothing here to cancel");
    }

    /// <summary>
    /// The names are matched as text, so a rename in .NET or in this repo would make the rule silently vacuous.
    /// This is the compensating fact.
    /// </summary>
    [Fact]
    public void Every_forbidden_name_still_resolves_to_a_real_type()
    {
        _forbidden.Select(Resolve).ShouldNotContain((Type?)null);

        // The two the walk would most plausibly stop matching, named in code so a rename breaks the build here.
        Resolve("System.Net.Http.IHttpClientFactory").ShouldBe(typeof(IHttpClientFactory));
        Resolve("System.Net.Sockets.Socket").ShouldBe(typeof(Socket));
        Resolve("MMLib.Alvo.Events.IEmailSender").ShouldBe(typeof(IEmailSender));
    }

    /// <summary>
    /// The positive control, and the reason this is a dependency walk rather than a look at one constructor: a
    /// client two hops away — held as a field by something the constructor takes — is reported.
    /// </summary>
    [Fact]
    public void The_scan_reports_a_client_two_hops_from_the_constructor()
        => Offenders(Closure(typeof(TakesSomethingThatHoldsAClient))).ShouldNotBeEmpty();

    /// <summary>The negative control: a chain that holds no client is not reported.</summary>
    [Fact]
    public void The_scan_reports_nothing_for_a_chain_that_holds_no_client()
        => Offenders(Closure(typeof(TakesSomethingClean))).ShouldBeEmpty();

    /// <summary>The deliberately offending chain: one hop of indirection over an <see cref="HttpClient"/>.</summary>
    /// <param name="notifier">The dependency that holds the client.</param>
    private sealed class TakesSomethingThatHoldsAClient(Notifier notifier)
    {
        public override string ToString() => notifier.ToString()!;
    }

    /// <summary>A dependency whose <em>name</em> says nothing and whose field says everything.</summary>
    /// <remarks>
    /// The client is never created — the field's declared type is all the scan reads, and a control that
    /// constructed one would own a disposable it has no reason to own.
    /// </remarks>
    private sealed class Notifier(HttpClient? client)
    {
        private readonly HttpClient? _client = client;

        public override string ToString() => _client?.ToString() ?? string.Empty;
    }

    /// <summary>The clean chain, so the scan is not merely reporting everything it is handed.</summary>
    /// <param name="clock">A dependency with no reach of its own.</param>
    private sealed class TakesSomethingClean(TimeProvider clock)
    {
        public override string ToString() => clock.ToString()!;
    }

    private static IReadOnlyList<string> Offenders(IReadOnlyCollection<Type> closure) =>
        [.. closure.Where(IsForbidden).Select(Name).Order(StringComparer.Ordinal)];

    private static bool IsForbidden(Type type) => _forbidden.Contains(Name(type), StringComparer.Ordinal);

    private static string Name(Type type) =>
        (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName ?? type.Name;

    /// <summary>
    /// Every type reachable from <paramref name="seed"/>'s constructors and fields, transitively.
    /// </summary>
    /// <remarks>
    /// Only Alvo's own types are <em>followed</em> — everything else is checked and not descended into, because
    /// walking the BCL would reach a socket from a string builder eventually and the answer would mean nothing.
    /// An injected interface or abstract type follows every implementation the shipped assemblies declare,
    /// since that is the set a container may resolve it to.
    /// </remarks>
    /// <param name="seed">The type whose dependency closure to compute.</param>
    private static HashSet<Type> Closure(Type seed)
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>([seed]);

        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!seen.Add(type) || !IsOurs(type))
            {
                continue;
            }

            foreach (var next in Dependencies(type).SelectMany(Expand).Where(candidate => !seen.Contains(candidate)))
            {
                pending.Push(next);
            }
        }

        return seen;
    }

    private static IEnumerable<Type> Dependencies(Type type) =>
    [
        .. type.GetConstructors(AnyMember).SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType),
        .. Fields(type).Select(field => field.FieldType),
        .. Implementations(type),
    ];

    /// <summary>Every declared field, including a base type's — a dependency stored one level up is still held.</summary>
    private static FieldInfo[] Fields(Type type) =>
        type.BaseType is { } baseType && IsOurs(baseType)
            ? [.. type.GetFields(AnyMember), .. Fields(baseType)]
            : type.GetFields(AnyMember);

    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Every concrete type in the shipped assemblies assignable to <paramref name="type"/>, when it is one a
    /// container has to pick an implementation for.
    /// </summary>
    private static IEnumerable<Type> Implementations(Type type) =>
        type.IsInterface || type.IsAbstract
            ? _shipped.SelectMany(assembly => assembly.GetTypes())
                .Where(candidate => candidate is { IsClass: true, IsAbstract: false } && type.IsAssignableFrom(candidate))
            : [];

    private static readonly Assembly[] _shipped =
        [typeof(BeforeHookRunner).Assembly, typeof(IBeforeHookRunner).Assembly];

    /// <summary>A type plus everything reachable through it — generic arguments, array elements, by-ref targets.</summary>
    private static IEnumerable<Type> Expand(Type type) =>
    [
        type,
        .. type.HasElementType ? Expand(type.GetElementType()!) : [],
        .. type.GenericTypeArguments.SelectMany(Expand),
    ];

    /// <summary>
    /// Whether a type is one of ours, and therefore one whose own dependencies are part of the closure. The
    /// test assembly counts, so the two controls above are walked exactly as the runner is.
    /// </summary>
    private static bool IsOurs(Type type) =>
        type.Assembly.GetName().Name?.StartsWith("MMLib.Alvo", StringComparison.Ordinal) == true;

    /// <summary>
    /// One forbidden name as a type, looked up across every assembly that could declare one. Named assemblies
    /// rather than a scan of everything loaded, because <c>IHttpClientFactory</c> is the case that breaks the
    /// naive answer: it lives in <c>Microsoft.Extensions.Http</c>, not in <c>System.Net.Http</c>, so
    /// <see cref="Type.GetType(string)"/> finds nothing for it.
    /// </summary>
    /// <param name="name">The forbidden type's full name.</param>
    private static Type? Resolve(string name) =>
        _searched.Select(assembly => assembly.GetType(name)).FirstOrDefault(found => found is not null)
        ?? Type.GetType(name);

    private static readonly Assembly[] _searched =
    [
        .. _shipped,
        typeof(HttpClient).Assembly,
        typeof(IHttpClientFactory).Assembly,
        typeof(Socket).Assembly,
    ];
}
