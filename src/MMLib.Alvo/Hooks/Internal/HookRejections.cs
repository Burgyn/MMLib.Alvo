using MMLib.Alvo.Api;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// What one hook recorded with <c>Invalid</c>, <c>Conflict</c> and <c>Forbidden</c> (spec 2026-10-14 §4.4.2): the winning kind by
/// precedence — 403 over 409 over 422 — with every violation of that kind. Only a declared field the descriptor never hides is
/// pointed at; anything else — a hidden field, an undeclared name, a case variant — gets the body pointer (Rulings 17, 53).
/// </summary>
/// <remarks>Safe to record into from tasks a hook runs in parallel.</remarks>
/// <param name="extension">The hook's id.</param>
/// <param name="pointable">The entity's declared fields that carry no <c>hidden</c> flag, compared ordinally.</param>
internal sealed class HookRejections(string extension, IReadOnlySet<string> pointable)
{
    private readonly List<(AlvoWriteRejectionKind Kind, AlvoViolation Violation)> _recorded = [];
    private readonly Lock _gate = new();

    internal void Record(AlvoWriteRejectionKind kind, string code, string message, string? field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var violation = new AlvoViolation(PointerOf(field), code, message, null);
        lock (_gate)
        {
            _recorded.Add((kind, violation));
        }
    }

    internal AlvoWriteRejectedException? Winner()
    {
        lock (_gate)
        {
            if (_recorded.Count == 0)
            {
                return null;
            }

            var kind = _recorded.Select(recorded => recorded.Kind).MaxBy(Precedence);

            return new AlvoWriteRejectedException(kind, extension, [.. _recorded.Where(recorded => recorded.Kind == kind).Select(recorded => recorded.Violation)]);
        }
    }

    private string PointerOf(string? field) =>
        field is not null && pointable.Contains(field) ? PayloadViolations.PointerTo(field) : PayloadViolations.BodyPointer;

    private static int Precedence(AlvoWriteRejectionKind kind) => kind switch
    {
        AlvoWriteRejectionKind.Forbidden => 3,
        AlvoWriteRejectionKind.Conflict => 2,
        _ => 1,
    };
}
