using MMLib.Alvo.Api.Internal;
using System.Reflection;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The core grants the standalone host <c>InternalsVisibleTo</c> for the sake of one type, <see cref="ClientPartition"/>;
/// the grant reaches every core internal, so this holds the host's member references to that one — read from the
/// compiled metadata, not the source.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it checks:</b> every member reference in the host's IL (a call, a field read or write, a constructor) whose
/// declaring type is a non-generic core type that is not visible outside the core. The only one allowed is
/// <see cref="ClientPartition.Of"/>.
/// </para>
/// <para>
/// <b>What it does not check:</b> an internal member of a <em>public</em> core type (visibility is judged on the declaring
/// type), a member of a generic internal type (its parent is a type specification, which the scan skips), and a reference
/// to an internal type with no member (<c>typeof</c>, a field's, parameter's or base type). Those stay a reviewer's to
/// catch.
/// </para>
/// </remarks>
public sealed class HostInternalGrantTests
{
    private static readonly Assembly _core = typeof(ClientPartition).Assembly;

    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(AlvoHost).Assembly);

    /// <summary>The control: the scan sees the one internal the host is granted for.</summary>
    [Fact]
    public void The_scan_sees_the_partition_the_credential_limit_charges_under()
        => _referenced.ShouldContain($"{typeof(ClientPartition).FullName}::{nameof(ClientPartition.Of)}");

    [Fact]
    public void The_only_member_of_an_internal_core_type_the_host_references_is_the_client_partition()
        => _referenced.Where(IsCoreInternal).ShouldBe([$"{typeof(ClientPartition).FullName}::{nameof(ClientPartition.Of)}"]);

    private static bool IsCoreInternal(string member)
        => _core.GetType(member[..member.IndexOf("::", StringComparison.Ordinal)].Replace('/', '+')) is { IsVisible: false };
}
