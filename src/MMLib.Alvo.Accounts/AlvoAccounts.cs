namespace MMLib.Alvo.Accounts;

/// <summary>
/// End-user sign-in's wire-level name: the configuration section it is configured from.
/// </summary>
public static class AlvoAccounts
{
    /// <summary>The configuration section <see cref="AlvoAccountsOptions"/> binds from.</summary>
    /// <remarks>
    /// The <c>Alvo:*</c> spelling every other subsystem binds under (<c>Alvo:Admin</c>, <c>Alvo:Secrets</c>,
    /// <c>Alvo:Api</c>). Every startup refusal names its setting under this path, so an operator can find it.
    /// </remarks>
    public const string ConfigurationSection = "Alvo:Accounts";
}
