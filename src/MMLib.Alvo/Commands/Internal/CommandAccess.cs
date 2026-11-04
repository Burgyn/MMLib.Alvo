using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command's access rule as CEL text — written by the host, or translated from a typed lambda.</summary>
/// <param name="Source">The CEL text, exactly as the catalog shows it.</param>
internal sealed record CommandAccess(string Source)
{
    /// <summary>A rule written as CEL; an empty one refuses registration (spec 2026-10-14 §4.9, §6.2).</summary>
    /// <param name="access">The CEL text.</param>
    /// <exception cref="ArgumentException"><paramref name="access"/> is null, empty or whitespace.</exception>
    internal static CommandAccess Of(string? access) =>
        string.IsNullOrWhiteSpace(access)
            ? throw new ArgumentException(
                "A command needs an access rule: nothing is reachable without one. Write the CEL condition that admits its "
                + "callers — 'staff' in @user.roles — or 'true' to admit every caller, anonymous ones included.",
                nameof(access))
            : new CommandAccess(access);

    /// <summary>A caller-only rule written as a lambda.</summary>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name, for a refusal.</param>
    internal static CommandAccess Typed(Expression<Func<AlvoConditionCaller, bool>> access, string name) =>
        new(AccessTranslator.Translate(access, name));

    /// <summary>A row-and-caller rule written as a lambda.</summary>
    /// <typeparam name="TEntity">The typed record of the bound entity.</typeparam>
    /// <param name="access">The lambda.</param>
    /// <param name="name">The command's name, for a refusal.</param>
    internal static CommandAccess Typed<TEntity>(Expression<Func<TEntity, AlvoConditionCaller, bool>> access, string name)
        where TEntity : class =>
        new(AccessTranslator.Translate(access, name));
}
