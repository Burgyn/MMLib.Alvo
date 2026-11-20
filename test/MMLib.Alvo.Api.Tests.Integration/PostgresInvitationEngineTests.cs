using MMLib.Alvo.Tests.Accounts;
using Xunit;

namespace MMLib.Alvo.Api.Tests.Integration;

/// <summary>PostgreSQL's leg of <see cref="InvitationEngineFacts"/>: not one assertion of its own.</summary>
/// <remarks>
/// The leg where two accepts of one single-use code really overlap in the engine — no in-process write gate serialises
/// them, so the invitation's conditional <c>UPDATE</c> (S4 plan D4 step 5) is what leaves one member.
/// </remarks>
public sealed class PostgresInvitationEngineTests : InvitationEngineFacts, IAsyncLifetime
{
    private readonly PostgresApiEngine _engine = new();

    /// <inheritdoc/>
    protected override AlvoApiEngine Engine => _engine;

    /// <inheritdoc/>
    public ValueTask InitializeAsync() => _engine.InitializeAsync();

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
