namespace MMLib.Alvo.Events;

/// <summary>The caller a typed condition tests — translated into <c>@user</c> and <c>@tenant</c>, never run.</summary>
public sealed class AlvoConditionCaller
{
    private const string TranslationOnly = "An AlvoConditionCaller is read by Alvo's translator and never run: pass the lambda to when:.";

    private AlvoConditionCaller()
    {
    }

    /// <summary>Gets the caller's user id (<c>@user.id</c>).</summary>
    public Guid? User => throw new NotSupportedException(TranslationOnly);

    /// <summary>Gets the caller's tenant id (<c>@tenant.id</c>).</summary>
    public Guid? Tenant => throw new NotSupportedException(TranslationOnly);

    /// <summary>Whether the caller holds <paramref name="role"/> (<c>'role' in @user.roles</c>).</summary>
    /// <param name="role">A role the descriptor declares, as a constant.</param>
    /// <returns>Never returns: the call is translated.</returns>
    public bool HasRole(string role) => throw new NotSupportedException(TranslationOnly);
}
