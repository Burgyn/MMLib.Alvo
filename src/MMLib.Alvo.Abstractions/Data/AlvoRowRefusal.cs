namespace MMLib.Alvo.Data;

/// <summary>One row of a batch, and why the port refused it.</summary>
/// <remarks>
/// <para>
/// <b>This exists because a batch's refusal has to name a row.</b>
/// <see cref="AlvoAuthorizationException"/> carries a message and nothing else, so a five-hundred-row import
/// refused with "forbidden" is one nobody can fix — and a batch is one transaction, so the caller cannot
/// bisect it by retrying halves without writing the halves that pass.
/// </para>
/// <para>
/// <b>An index rather than a pointer, and that is the truer type.</b> The port knows a row's position in the
/// list it was handed; a JSON Pointer is a fact about a request body, which the port never sees. A request
/// layer composes <c>/rows/3/quoted_price</c> where it already composes pointers, from this index and the
/// field its own reader bound.
/// </para>
/// <para>
/// <b>The message rule is a port obligation, and it is the one an implementor is most likely to break.</b>
/// <see cref="Message"/> and <see cref="FixSuggestion"/> must be built from constants and server-owned
/// values — never from the caller's own keys or values. A refusal is answered before much else, so it is the
/// cheapest oracle a framework has: a message naming a field answers "does this entity have one" a request
/// at a time, and a message quoting a value puts attacker-controlled bytes into every log that records the
/// response. The shipped implementations are held to it by the contract suite.
/// </para>
/// </remarks>
/// <param name="Index">The row's position in the list the caller supplied, counting from zero.</param>
/// <param name="Code">A stable kebab-case code, e.g. <c>forbidden</c>, <c>required</c>, <c>row-not-found</c>.</param>
/// <param name="Message">A human sentence, free of caller-supplied text.</param>
/// <param name="FixSuggestion">What to change, or <see langword="null"/> when the source has nothing to offer.</param>
public sealed record AlvoRowRefusal(int Index, string Code, string Message, string? FixSuggestion);
