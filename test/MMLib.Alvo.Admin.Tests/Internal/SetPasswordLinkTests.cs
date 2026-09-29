using Microsoft.AspNetCore.Components;
using MMLib.Alvo.Admin.Internal;
using System.Web;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The set-password link: the page under the dashboard's own base, and the address and token in its fragment,
/// escaped so a browser's <c>URLSearchParams</c> gives both back exactly.
/// </summary>
public sealed class SetPasswordLinkTests
{
    /// <summary>A token with every character base64 and an address can carry that a fragment would misread.</summary>
    private const string Token = "CfDJ8+a/b=c&d#e@f+==";

    [Fact]
    public void The_link_is_the_page_under_the_dashboards_base_with_both_values_in_the_fragment()
    {
        var link = SetPasswordLink.For(new FixedNavigation("https://alvo.example/tenant-a/"), "eva+ops@alvo.test", Token);

        link.ShouldStartWith($"https://alvo.example/tenant-a{AlvoAdmin.SetPasswordPath}#email=");
        link.ShouldNotContain("?", Case.Sensitive, "nothing of the token may reach a request line");
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('=')]
    [InlineData('@')]
    [InlineData('&')]
    [InlineData('#')]
    public void A_character_the_fragment_would_misread_is_escaped(char character)
    {
        var fragment = SetPasswordLink.Fragment("eva@alvo.test", Token)["#email=".Length..];

        fragment.Split("&token=")[1].ShouldNotContain(character.ToString(), Case.Sensitive);
        fragment.Split("&token=")[0].ShouldNotContain(character.ToString(), Case.Sensitive);
    }

    /// <summary>
    /// <b>The round trip admin.js makes</b>: <c>URLSearchParams</c> reads a <c>+</c> as a space, so a token that
    /// escaped only some characters would come back broken; <see cref="HttpUtility.ParseQueryString(string)"/> has
    /// the same form-decoding rule.
    /// </summary>
    [Fact]
    public void Url_search_params_semantics_give_the_address_and_the_token_back()
    {
        var fragment = SetPasswordLink.Fragment("eva+ops@alvo.test", Token);

        var read = HttpUtility.ParseQueryString(fragment[1..]);

        read["email"].ShouldBe("eva+ops@alvo.test");
        read["token"].ShouldBe(Token);
    }

    /// <summary>A navigation manager at a fixed address, which is all the link reads.</summary>
    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string baseUri) => Initialize(baseUri, baseUri);
    }
}
