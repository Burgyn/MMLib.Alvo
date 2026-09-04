using MMLib.Alvo.Data;

namespace MMLib.Alvo.Abstractions.Tests.Data;

/// <summary>
/// The guards <see cref="AlvoQuery"/> owns on behalf of every <see cref="IAlvoData"/> implementation.
/// A rule of the port lives on the port's own type so a future implementation inherits it rather than
/// writing another copy — and each is asserted at the boundary it draws, because the whole value of a
/// guard is that its answer is the same wherever it is called from.
/// </summary>
public class AlvoQueryTests
{
    /// <summary>
    /// An empty projection could return no field at all. Refused rather than read as "every field", on
    /// the same ground the <c>after</c>/<c>offset</c> pair is refused: silently resolving an ambiguous
    /// request is what this port does not do.
    /// </summary>
    [Fact]
    public void A_projection_that_names_no_field_is_refused_rather_than_read_as_every_field()
    {
        var query = new AlvoQuery { Entity = "vehicles", Select = [] };

        Should.Throw<ArgumentException>(() => AlvoQuery.EnsureProjectionIsSane(query));
    }

    /// <summary>
    /// The distinction the member rests on: <see langword="null"/> is "every field this caller may read"
    /// and is the shape every pre-projection caller sends, so it cannot be refused.
    /// </summary>
    [Fact]
    public void An_absent_projection_is_every_field_and_is_not_refused()
    {
        var query = new AlvoQuery { Entity = "vehicles" };

        query.Select.ShouldBeNull();
        Should.NotThrow(() => AlvoQuery.EnsureProjectionIsSane(query));
    }

    [Fact]
    public void A_projection_naming_one_field_is_accepted()
    {
        var query = new AlvoQuery { Entity = "vehicles", Select = ["name"] };

        Should.NotThrow(() => AlvoQuery.EnsureProjectionIsSane(query));
    }

    [Fact]
    public void The_projection_guard_requires_a_query()
    {
        Should.Throw<ArgumentNullException>(() => AlvoQuery.EnsureProjectionIsSane(null!));
    }
}
