namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// The one assumption both <c>in</c> backends share, asserted in one place. Alvo's <c>in</c> is role
/// membership and nothing else: <see cref="SqlPredicateRenderer"/> decides it at render time from the
/// caller's role set and <see cref="CelInterpreter"/> evaluates it from the same set, so neither reads
/// the expression's right operand at all. That is correct only because
/// <see cref="CelTypeChecker"/> admits no <see cref="CelValueType.StringList"/> producer other than
/// <c>@user.roles</c> — an assumption about a different file, which is exactly the kind that rots
/// silently. The day a second string-list context value lands (<c>@user.claims</c>, <c>@user.teams</c>),
/// membership against it must fail loudly rather than be answered from the role set.
/// </summary>
internal static class RoleMembership
{
    /// <summary>Requires that an <c>in</c> expression's right operand is <c>@user.roles</c>.</summary>
    /// <param name="right">The right operand of a compiled <see cref="CelBinaryOperator.In"/> node.</param>
    /// <exception cref="NotSupportedException"><paramref name="right"/> is anything but <c>@user.roles</c>.</exception>
    public static void RequireUserRolesOperand(CelNode right)
    {
        if (right is CelContextRef { Value: CelContextValue.UserRoles })
        {
            return;
        }

        throw new NotSupportedException(
            "'in' is only supported with @user.roles as its right operand; both expression backends answer it "
            + "from the caller's role set, so any other operand would be evaluated as if it were that set.");
    }
}
