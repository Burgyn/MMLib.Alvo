using MMLib.Alvo.Api.Internal;
using System.Reflection;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// The core grants the standalone host <c>InternalsVisibleTo</c> for one type, <see cref="ClientPartition"/>; the grant
/// reaches every core internal, so this holds the host to that one — read from the compiled metadata, not the source.
/// </summary>
public sealed class HostInternalGrantTests
{
    private static readonly Assembly _core = typeof(ClientPartition).Assembly;

    private static readonly IReadOnlySet<string> _referenced = InternalMemberReferences.Of(typeof(AlvoHost).Assembly);

    /// <summary>The control: the scan sees the one internal the host is granted for.</summary>
    [Fact]
    public void The_scan_sees_the_partition_the_credential_limit_charges_under()
        => _referenced.ShouldContain($"{typeof(ClientPartition).FullName}::{nameof(ClientPartition.Of)}");

    [Fact]
    public void The_only_core_internal_the_host_references_is_the_client_partition()
        => _referenced.Where(IsCoreInternal).ShouldBe([$"{typeof(ClientPartition).FullName}::{nameof(ClientPartition.Of)}"]);

    private static bool IsCoreInternal(string member)
        => _core.GetType(member[..member.IndexOf("::", StringComparison.Ordinal)].Replace('/', '+')) is { IsVisible: false };
}
