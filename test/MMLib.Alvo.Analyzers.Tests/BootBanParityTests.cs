using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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

    /// <summary>
    /// Both sides refuse a mail sender by its name alone, in any namespace and generic or not; the boot ban sees the
    /// reflection name (<c>IEmailSender`1</c>), the analyzer the symbol name (<c>IEmailSender</c>), and they must agree.
    /// </summary>
    [Theory]
    [InlineData(typeof(Mail.IEmailSender), "Acme.Mail.IEmailSender")]
    [InlineData(typeof(Mail.IEmailSender<>), "Acme.Mail.IEmailSender`1")]
    [InlineData(typeof(Mail.IEmailSenderFactory), "Acme.Mail.IEmailSenderFactory")]
    public void The_analyzer_matches_a_mail_sender_by_the_boot_ban_s_own_name(Type type, string metadataName)
    {
        var symbol = _mail.Value.GetTypeByMetadataName(metadataName).ShouldNotBeNull();

        IoReach.IsReached(symbol).ShouldBe(HookDependencies.IsBanned(type));
    }

    private static readonly Lazy<Compilation> _mail = new(() => CSharpCompilation.Create(
        "Mail",
        [CSharpSyntaxTree.ParseText("namespace Acme.Mail { public interface IEmailSender; public interface IEmailSender<T>; public interface IEmailSenderFactory; }", cancellationToken: TestContext.Current.CancellationToken)],
        [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]));

    /// <summary>The reflection twins of the source <see cref="_mail"/> compiles.</summary>
    public static class Mail
    {
        public interface IEmailSender;

        public interface IEmailSender<T>;

        public interface IEmailSenderFactory;
    }
}
