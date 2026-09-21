namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Endpoint metadata naming the <see cref="IAlvoManagement"/> member a route stands for, and the
/// <see cref="ManagementOperation"/> its gate is keyed on.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Member"/> is always written with <c>nameof</c>.</b> That is what makes the contract test
/// structural rather than stringly-typed: renaming an interface member breaks the build at the mapping site,
/// and deleting one breaks the test.
/// </para>
/// <para>
/// <b>It carries the operation, never a level.</b> The level an operation needs is
/// <see cref="ManagementOperations"/>'s one table — a second copy on the route would be a second authority
/// for the same answer, and the two would diverge the first time one of them was edited.
/// </para>
/// </remarks>
/// <param name="Member">The interface member's name, from <c>nameof</c>.</param>
/// <param name="Operation">The operation the route performs, as its gate names it.</param>
internal sealed record ManagementRoute(string Member, ManagementOperation Operation);
