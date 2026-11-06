using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// <c>receiver.member</c> straight off the parser, built only when a compile is given a relation catalog: a forward
/// path over a ref field, or a field of an <c>exists</c> variable. The type checker resolves it and it never survives
/// into a compiled tree.
/// </summary>
/// <param name="Receiver">The identifier before the dot.</param>
/// <param name="Member">The identifier after it.</param>
/// <param name="Position">Where the receiver starts in the source.</param>
internal sealed record CelPath(string Receiver, string Member, int Position) : CelNode;

/// <summary>
/// CEL's standard <c>receiver.exists(variable, body)</c> macro straight off the parser; the type checker turns it into
/// a reverse <see cref="CelRelation"/>.
/// </summary>
/// <param name="Receiver">The child as written: its name, or <c>&lt;child&gt;_via_&lt;field&gt;</c>.</param>
/// <param name="Variable">The name the body reads a child row by.</param>
/// <param name="Body">The condition over one child row.</param>
/// <param name="Position">Where the receiver starts in the source.</param>
internal sealed record CelExistsMacro(string Receiver, string Variable, CelNode Body, int Position) : CelNode;

/// <summary>
/// A type-checked forward path: <paramref name="Field"/> of the row <paramref name="Via"/> points at. The lifter turns
/// every comparison holding one into a forward <see cref="CelRelation"/>, so it never survives into a compiled tree.
/// </summary>
/// <param name="Via">The outer row's ref field.</param>
/// <param name="Related">The entity the ref points at.</param>
/// <param name="Field">The related row's field, typed against <paramref name="Related"/>.</param>
internal sealed record CelHop(string Via, EntitySchema Related, CelFieldRef Field) : CelNode;
