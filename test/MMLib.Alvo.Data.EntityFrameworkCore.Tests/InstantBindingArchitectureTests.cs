using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Every site that binds the instant every CEL <c>now()</c> reads (HX-11 Ruling 1), enumerated from the compiled IL of the two
/// assemblies allowed to bind it — <c>MMLib.Alvo</c> and the EF driver (Ruling 48) — and every entry point that can reach a
/// <c>now()</c> held to binding one, so a new entry point that evaluates CEL without an instant fails here rather than with
/// "now() has no instant" at its first request.
/// </summary>
/// <remarks>
/// <para>
/// <b>What binds.</b> <c>AlvoFlow.EnsureInstant</c> binds the clock's instant when none is bound and keeps a bound one, so
/// the outermost entry wins: an HTTP request's caller filter, the command filter, the embedded <c>IAlvoCommands</c>, the
/// management policy simulator, <c>TransactAsync</c>, and every data-port entry for an embedded call that arrives with none.
/// <c>AlvoFlow.EnterInstant</c> overrides it with an event's time around a post-commit condition only; the handler body
/// then runs on a fresh instant its own data calls bind (Ruling 58).
/// </para>
/// <para>
/// <b>Why the IL.</b> A site is a call the compiler emitted, so a wrapper the source names differently — <c>RequestInstant</c>,
/// <c>EfAlvoData.EnsureInstant</c> — is followed as one more binder rather than missed. Async state machines, lambdas and
/// local functions are folded into the member that declares them.
/// </para>
/// </remarks>
public class InstantBindingArchitectureTests
{
    private static readonly Assembly _core = typeof(Microsoft.Extensions.DependencyInjection.AlvoServiceCollectionExtensions).Assembly;
    private static readonly Assembly _driver = typeof(EfAlvoData).Assembly;

    /// <summary>The members whose call binds an instant: the two flow setters and the wrappers over them.</summary>
    private static readonly ImmutableHashSet<string> _binders =
    [
        "MMLib.Alvo.AlvoFlow::EnterInstant",
        "MMLib.Alvo.AlvoFlow::EnsureInstant",
        "MMLib.Alvo.Api.Internal.RequestInstant::Enter",
        "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::EnsureInstant",
    ];

    /// <summary>Every binding site, with why it binds there. Adding or removing one is a decision this list records.</summary>
    private static readonly IReadOnlyDictionary<string, string> _sites = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MMLib.Alvo.Api.Internal.RequestInstant::Enter"] = "the request's instant, from the host's clock (wrapper)",
        ["MMLib.Alvo.Api.Internal.AlvoContextFilter::InvokeAsync"] = "a Data API request, where the caller is resolved",
        ["MMLib.Alvo.Api.Internal.AlvoCallerFilter::InvokeAsync"] = "a host route that asks for Alvo's caller",
        ["MMLib.Alvo.Management.Internal.ManagementCallerFilter::InvokeAsync"] = "a Management API request",
        ["MMLib.Alvo.Commands.Internal.CommandEndpointFilter::InvokeAsync"] = "a command invoked over HTTP",
        ["MMLib.Alvo.Commands.Internal.AlvoCommands::InvokeAsync"] = "a command invoked in-process (IAlvoCommands)",
        ["MMLib.Alvo.Management.Internal.AlvoManagementService::SimulatePolicyAsync"] = "the policy simulator, which judges a rule",
        ["MMLib.Alvo.Events.Internal.HandlerCondition::Judge"] = "a handler's condition, at the event's time (Ruling 58)",
        ["MMLib.Alvo.Events.Internal.ActionValues::Evaluate"] = "a descriptor action's values, at the event's time",
        ["MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::EnsureInstant"] = "the data port's binder, for TransactAsync (wrapper)",
        ["MMLib.Alvo.Data.EntityFrameworkCore.Internal.EfAlvoTransactions::TransactAsync"] = "one instant for a whole unit",
    };

    /// <summary>The interfaces whose implementations are entry points: a call into one may evaluate a <c>now()</c>.</summary>
    private static readonly ImmutableHashSet<string> _entryContracts =
    [
        "Microsoft.AspNetCore.Http.IEndpointFilter",
        "MMLib.Alvo.Data.IAlvoData",
        "MMLib.Alvo.Data.IAlvoTransactions",
        "MMLib.Alvo.Commands.IAlvoCommands",
        "MMLib.Alvo.Management.IAlvoManagement",
    ];

    /// <summary>Entry members that bind nothing, each with why none is needed.</summary>
    private static readonly IReadOnlyDictionary<string, string> _exempt = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MMLib.Alvo.Api.Internal.NoStoreResponseFilter::InvokeAsync"] = "sets a response header; evaluates no CEL",
        ["MMLib.Alvo.Api.Internal.AlvoCors/CorsFilter::InvokeAsync"] = "writes the CORS response headers; evaluates no CEL",
        ["MMLib.Alvo.Api.Internal.AlvoRateLimits/Charged::InvokeAsync"] = "charges a per-client budget; evaluates no CEL",
        ["MMLib.Alvo.Management.Internal.ManagementAccessEndpointFilter::InvokeAsync"] =
            "judges the access CEL profile, which refuses now() when it compiles (CelTypeChecker.AccessNoInstantMessage)",
        ["MMLib.Alvo.Data.IAlvoTransactions::TransactAsync"] =
            "the contract's default body for the untyped overload; it calls the typed TransactAsync, which binds",
        [$"{Management}::GetInfoAsync"] = "reads the host's identity; evaluates no CEL",
        [$"{Management}::ListProjectsAsync"] = "lists the served projects; evaluates no CEL",
        [$"{Management}::GetDescriptorAsync"] = "reads the stored descriptor; evaluates no CEL",
        [$"{Management}::ListRevisionsAsync"] = "reads the version history; evaluates no CEL",
        [$"{Management}::GetRevisionAsync"] = "reads one stored revision; evaluates no CEL",
        [$"{Management}::GetSchemaAsync"] = "reads the applied schema; evaluates no CEL",
        [$"{Management}::GetCapabilitiesAsync"] = "reads the host's capabilities; evaluates no CEL",
        [$"{Management}::GetCelFunctionsAsync"] = "lists the CEL catalog; evaluates no CEL",
        [$"{Management}::GetExtensionsAsync"] = "lists the extension catalog; evaluates no CEL",
        [$"{Management}::CheckExpressionAsync"] = "compiles and type-checks one expression; never evaluates it",
        [$"{Management}::ApplyDescriptorAsync"] =
            "compiles and migrates; a CHECK and a rollup filter refuse now() at compile time, so the DDL and backfill read none",
        [$"{Management}::RollbackAsync"] = "an apply of a stored revision; reads no now() for the same reason",
        [$"{Management}::SetAiConnectionAsync"] = "stores a connection; evaluates no CEL",
    };

    private const string Management = "MMLib.Alvo.Management.Internal.AlvoManagementService";

    [Fact]
    public void Every_instant_binding_site_is_enumerated()
    {
        var found = BindingSites().Order(StringComparer.Ordinal).ToList();

        found.ShouldBe(_sites.Keys.Order(StringComparer.Ordinal), ignoreOrder: false,
            "a site was added or removed: record it, with why it binds, in _sites (HX-11 Ruling 1)");
    }

    [Fact]
    public void The_data_port_binds_at_every_entry()
    {
        var entries = BindingCallers(_driver)
            .Where(site => site.StartsWith("MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        entries.ShouldBe(
            [
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::CreateAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::CreateManyAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::DeleteAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::DeleteManyAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::EnsureInstant",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::GetAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::QueryAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::ReplaceAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::ReplanTimersAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::UpdateAsync",
                "MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::UpdateManyAsync",
            ],
            ignoreOrder: false,
            "every IAlvoData member, and the timer re-plan the core calls, binds an instant for an embedded call");
    }

    public static TheoryData<string> EntryMembers() => [.. EntryMemberNames()];

    /// <summary>
    /// Per member, not per type: each method that implements an entry contract binds an instant itself, or is exempt with a
    /// reason — so a new member on a type that already binds elsewhere is caught (Task 12 review m1).
    /// </summary>
    [Theory]
    [MemberData(nameof(EntryMembers))]
    public void Every_entry_member_binds_an_instant_or_says_why_not(string member)
    {
        var binds = BindingCallers(_core).Concat(BindingCallers(_driver)).Contains(member);

        (binds || _exempt.ContainsKey(member)).ShouldBeTrue(
            $"'{member}' can evaluate a now() and binds no instant: bind one with AlvoFlow.EnsureInstant or RequestInstant.Enter, "
            + "or record in _exempt why it needs none");
    }

    [Fact]
    public void Every_exemption_names_a_live_entry_member_that_binds_nothing()
    {
        var members = EntryMemberNames().ToHashSet(StringComparer.Ordinal);
        var callers = BindingCallers(_core).Concat(BindingCallers(_driver)).ToHashSet(StringComparer.Ordinal);

        _exempt.Keys.ShouldAllBe(name => members.Contains(name) && !callers.Contains(name));
    }

    /// <summary>The scan sees what it must, so an empty answer cannot pass for "no site".</summary>
    [Fact]
    public void The_scan_sees_the_request_filter_and_the_unit()
    {
        BindingCallers(_core).ShouldContain("MMLib.Alvo.Api.Internal.AlvoContextFilter::InvokeAsync");
        BindingCallers(_driver).ShouldContain("MMLib.Alvo.Data.EntityFrameworkCore.Internal.EfAlvoTransactions::TransactAsync");
        EntryPointTypes().ShouldContain(typeof(EfAlvoData));
        EntryMemberNames().ShouldContain("MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::QueryAsync");
    }

    private static IEnumerable<string> BindingSites()
    {
        var callers = BindingCallers(_core).Concat(BindingCallers(_driver)).ToHashSet(StringComparer.Ordinal);
        // The data port's own entries are counted by The_data_port_binds_at_every_entry; here it is one site per wrapper.
        return callers.Where(site =>
            !site.StartsWith("MMLib.Alvo.Data.EntityFrameworkCore.EfAlvoData::", StringComparison.Ordinal)
            || _binders.Contains(site));
    }

    private static IEnumerable<Type> EntryPointTypes() =>
        new[] { _core, _driver }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && !type.Name.StartsWith('<'))
            .Where(type => type.GetInterfaces().Any(contract => _entryContracts.Contains(contract.FullName ?? string.Empty)));

    /// <summary>
    /// Every method implementing an entry contract, as <c>Namespace.Type::Member</c> — the name the IL walk folds a body
    /// into. Overloads share one name, so an overload binds when any body of that name does.
    /// </summary>
    private static IEnumerable<string> EntryMemberNames() =>
        EntryPointTypes()
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => _entryContracts.Contains(contract.FullName ?? string.Empty))
                .SelectMany(contract => type.GetInterfaceMap(contract).TargetMethods)
                .Select(method => $"{method.DeclaringType!.FullName!.Replace('+', '/')}::{method.Name}"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

    private static readonly Dictionary<Assembly, IReadOnlySet<string>> _callers = [];

    /// <summary>Every member of <paramref name="assembly"/> whose IL calls a binder, as <c>Namespace.Type::Member</c>.</summary>
    private static IReadOnlySet<string> BindingCallers(Assembly assembly)
    {
        lock (_callers)
        {
            if (!_callers.TryGetValue(assembly, out var found))
            {
                found = IlCalls.Callers(assembly, _binders);
                _callers.Add(assembly, found);
            }

            return found;
        }
    }

    /// <summary>A minimal IL walk: which members call which, read from the metadata.</summary>
    private static class IlCalls
    {
        private static readonly Dictionary<short, OpCode> _opCodes = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

        internal static HashSet<string> Callers(Assembly assembly, IReadOnlySet<string> callees)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var reader = new PEReader(stream);
            var metadata = reader.GetMetadataReader();
            var callers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var handle in metadata.MethodDefinitions)
            {
                var method = metadata.GetMethodDefinition(handle);
                if (method.RelativeVirtualAddress != 0
                    && Calls(reader.GetMethodBody(method.RelativeVirtualAddress), metadata).Any(callees.Contains))
                {
                    callers.Add(Owner(metadata, method));
                }
            }

            return callers;
        }

        private static IEnumerable<string> Calls(MethodBodyBlock body, MetadataReader metadata)
        {
            var il = body.GetILReader();
            while (il.RemainingBytes > 0)
            {
                var code = Read(ref il);
                if (code.OperandType == OperandType.InlineMethod)
                {
                    if (Callee(metadata, MetadataTokens.EntityHandle(il.ReadInt32())) is { } callee)
                    {
                        yield return callee;
                    }
                }
                else
                {
                    Skip(ref il, code);
                }
            }
        }

        private static OpCode Read(ref BlobReader il)
        {
            var first = il.ReadByte();
            return first == 0xFE ? _opCodes[unchecked((short)(0xFE00 | il.ReadByte()))] : _opCodes[first];
        }

        private static void Skip(ref BlobReader il, OpCode code)
        {
            switch (code.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar:
                    il.Offset += 1;
                    break;
                case OperandType.InlineVar:
                    il.Offset += 2;
                    break;
                case OperandType.InlineI8 or OperandType.InlineR:
                    il.Offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    var targets = il.ReadInt32();
                    il.Offset += 4 * targets;
                    break;
                default:
                    il.Offset += 4;
                    break;
            }
        }

        private static string? Callee(MetadataReader metadata, EntityHandle token) => token.Kind switch
        {
            HandleKind.MethodDefinition => Named(metadata, metadata.GetMethodDefinition((MethodDefinitionHandle)token)),
            HandleKind.MemberReference => Referenced(metadata, metadata.GetMemberReference((MemberReferenceHandle)token)),
            HandleKind.MethodSpecification => Callee(metadata, metadata.GetMethodSpecification((MethodSpecificationHandle)token).Method),
            _ => null,
        };

        private static string? Referenced(MetadataReader metadata, MemberReference member) => member.Parent.Kind switch
        {
            HandleKind.TypeReference => $"{TypeName(metadata, (TypeReferenceHandle)member.Parent)}::{metadata.GetString(member.Name)}",
            HandleKind.TypeDefinition => $"{TypeName(metadata, (TypeDefinitionHandle)member.Parent)}::{metadata.GetString(member.Name)}",
            _ => null,
        };

        private static string Named(MetadataReader metadata, MethodDefinition method) =>
            $"{TypeName(metadata, method.GetDeclaringType())}::{metadata.GetString(method.Name)}";

        /// <summary>The member that declares <paramref name="method"/>, compiler-generated state machines and closures folded in.</summary>
        private static string Owner(MetadataReader metadata, MethodDefinition method)
        {
            var name = Declared(metadata.GetString(method.Name));
            var type = method.GetDeclaringType();
            while (metadata.GetString(metadata.GetTypeDefinition(type).Name) is var typeName && typeName.StartsWith('<'))
            {
                if (Declared(typeName) is { Length: > 0 } declared && !typeName.StartsWith("<>", StringComparison.Ordinal))
                {
                    name = declared;
                }

                type = metadata.GetTypeDefinition(type).GetDeclaringType();
            }

            return $"{TypeName(metadata, type)}::{name}";
        }

        // "<InvokeAsync>d__5" → "InvokeAsync"; "<Judge>b__0_0" → "Judge"; anything else unchanged.
        private static string Declared(string name) =>
            name.StartsWith('<') && name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end ? name[1..end] : name;

        private static string TypeName(MetadataReader metadata, TypeDefinitionHandle handle)
        {
            var type = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            return declaring.IsNil ? $"{metadata.GetString(type.Namespace)}.{name}" : $"{TypeName(metadata, declaring)}/{name}";
        }

        private static string TypeName(MetadataReader metadata, TypeReferenceHandle handle)
        {
            var type = metadata.GetTypeReference(handle);
            var name = metadata.GetString(type.Name);
            return type.ResolutionScope.Kind == HandleKind.TypeReference
                ? $"{TypeName(metadata, (TypeReferenceHandle)type.ResolutionScope)}/{name}"
                : $"{metadata.GetString(type.Namespace)}.{name}";
        }
    }
}
