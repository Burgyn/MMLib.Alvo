using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Reads;
using System.Linq.Expressions;

namespace ReadsDesk;

[AlvoEntity("notes")]
public sealed class Note
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Region { get; init; }

    public string? Display { get; init; }
}

public static class ReadsDeskExtensions
{
    public static IAlvoBuilder AddReadsDesk(this IAlvoBuilder alvo) => alvo
        .Entity<Note>(notes => notes
            .Narrow<OwnRegionNarrowing>("own-region")
            .Enrich<NoteLabel>("note-label"));

    public static IAlvoBuilder AddUntypedNarrowing(this IAlvoBuilder alvo) => alvo
        .Entity("notes", notes => notes.Narrow(
            "own-region",
            (QueryNarrowing query) => query.Caller.Roles.Any(role => role.Name == "auditor") ? null : new AlvoComparison("region", AlvoFilterOperator.Eq, "north")));
}

public sealed class OwnRegionNarrowing : IQueryNarrowing<Note>
{
    public Expression<Func<Note, bool>>? Narrow(QueryNarrowing query) =>
        query.Caller.Roles.Any(role => role.Name == "auditor") ? null : x => x.Region == "north";
}

public sealed class NoteLabel : IRecordEnricher<Note>
{
    public void Enrich(Enrich<Note> row) =>
        row.SetVirtual(x => x.Display, $"{row.Row.Title} ({row.Row.Region})");
}
