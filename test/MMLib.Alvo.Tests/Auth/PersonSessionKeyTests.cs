using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// A person principal's key id names its session, and only that shape does: the self-service routes read it back to tie a
/// credential write to the session it came from (stack review A-I1).
/// </summary>
public sealed class PersonSessionKeyTests
{
    [Fact]
    public void A_session_round_trips_through_its_key_id()
    {
        var session = Guid.NewGuid();

        PersonSessionKey.Of(session).ShouldBe("session:" + session.ToString("N"));
        PersonSessionKey.SessionOf(PersonSessionKey.Of(session)).ShouldBe(session);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("alvo_pat_0123456789abcdef0123456789abcdef")]
    [InlineData("session:")]
    [InlineData("session:not-a-guid")]
    [InlineData("SESSION:0123456789abcdef0123456789abcdef")]
    [InlineData("session:01234567-89ab-cdef-0123-456789abcdef")]
    public void Any_other_key_id_names_no_session(string? keyId) =>
        PersonSessionKey.SessionOf(keyId).ShouldBeNull();
}
