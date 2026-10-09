using MMLib.Alvo.Admin.Components.Integrations;

namespace MMLib.Alvo.Admin.Tests.Integrations;

/// <summary>The Integrations list never shows the credentials a URL carries before its host (final review M7).</summary>
public sealed class UrlCredentialsTests
{
    [Theory]
    [InlineData("https://user:pass@example.com/h", "https://***@example.com/h")]
    [InlineData("https://token@example.com", "https://***@example.com")]
    [InlineData("https://a@b:c@example.com:8443/h?x=1#f", "https://***@example.com:8443/h?x=1#f")]
    [InlineData("http://user:pass@127.0.0.1:5081/h", "http://***@127.0.0.1:5081/h")]
    public void The_userinfo_is_masked_and_the_host_and_path_are_kept(string url, string shown)
        => UrlCredentials.Masked(url).ShouldBe(shown);

    [Theory]
    [InlineData("https://example.com/h")]
    [InlineData("https://example.com/h?to=a@b.example")]
    [InlineData("https://example.com/a@b")]
    [InlineData("https://example.com#a@b")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("https://")]
    [InlineData("https:///path")]
    public void A_url_without_userinfo_is_shown_as_written(string url)
        => UrlCredentials.Masked(url).ShouldBe(url);
}
