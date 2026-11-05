using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Analyzers.Tests;

/// <summary>Every type and namespace the boot ban refuses at start is one ALVO1001 reports, so the two never disagree.</summary>
public sealed class BootBanParityTests
{
    [Fact]
    public void The_analyzer_names_every_type_the_boot_ban_refuses() =>
        HookDependencies.Banned.Select(type => type.FullName!).Except(IoReach.BannedTypes, StringComparer.Ordinal)
            .ShouldBeEmpty("a type the start refuses but the analyzer does not report is a refusal nobody saw coming");

    [Fact]
    public void The_analyzer_names_every_network_namespace_the_boot_ban_refuses() =>
        HookDependencies.NetworkNamespaces.Except(IoReach.NetworkNamespaces, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void The_analyzer_names_every_web_request_type_the_boot_ban_refuses() =>
        HookDependencies.WebRequestTypes.Except(IoReach.WebRequestTypes, StringComparer.Ordinal).ShouldBeEmpty();

    [Fact]
    public void The_analyzer_matches_the_identity_namespaces_by_the_boot_ban_s_own_prefixes() =>
        HookDependencies.IdentityPrefixes.Order(StringComparer.Ordinal).ShouldBe(IoReach.IdentityPrefixes.Order(StringComparer.Ordinal));
}
