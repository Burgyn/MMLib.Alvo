using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;

using NSubstitute;

namespace MMLib.Alvo.Tests.Extensions;

/// <summary>
/// A session is <see cref="IAlvoData"/> with the caller fixed: every call runs as the bound context, and no call can
/// carry an idempotency key of its own (spec 2026-10-14 §5, M12).
/// </summary>
public sealed class AlvoSessionsTests
{
    private static readonly AlvoContext _caller = AlvoContext.System(new TenantId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
    private static readonly Guid _id = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal) { ["title"] = "x" };

    private readonly IAlvoData _data = Substitute.For<IAlvoData>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_session_reports_the_caller_it_was_bound_to() =>
        new AlvoSessions(_data).For(_caller).Context.ShouldBeSameAs(_caller);

    [Fact]
    public async Task Reads_run_as_the_bound_caller()
    {
        var session = new AlvoSessions(_data).For(_caller);
        var query = new AlvoQuery { Entity = "tickets" };

        await session.QueryAsync(query, Ct);
        await session.GetAsync("tickets", _id, Ct);

        await _data.Received(1).QueryAsync(query, _caller, Ct);
        await _data.Received(1).GetAsync("tickets", _id, _caller, Ct);
    }

    [Fact]
    public async Task Writes_run_as_the_bound_caller_with_no_idempotency_key()
    {
        var session = new AlvoSessions(_data).For(_caller);
        var forUpdate = new AlvoPrecondition(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero));
        var forReplace = new AlvoPrecondition(new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        var forDelete = new AlvoPrecondition(new DateTimeOffset(2026, 10, 23, 11, 0, 0, TimeSpan.Zero));

        await session.CreateAsync("tickets", _values, Ct);
        await session.UpdateAsync("tickets", _id, _values, forUpdate, Ct);
        await session.ReplaceAsync("tickets", _id, _values, forReplace, Ct);
        await session.DeleteAsync("tickets", _id, forDelete, Ct);

        await _data.Received(1).CreateAsync("tickets", _values, _caller, null, Ct);
        await _data.Received(1).UpdateAsync("tickets", _id, _values, _caller, forUpdate, null, Ct);
        await _data.Received(1).ReplaceAsync("tickets", _id, _values, _caller, forReplace, null, Ct);
        await _data.Received(1).DeleteAsync("tickets", _id, _caller, forDelete, null, Ct);
    }

    [Fact]
    public void A_session_needs_a_caller() =>
        Should.Throw<ArgumentNullException>(() => new AlvoSessions(_data).For(null!));
}
