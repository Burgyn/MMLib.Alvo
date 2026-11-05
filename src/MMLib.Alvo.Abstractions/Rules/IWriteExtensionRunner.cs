using MMLib.Alvo.Data;

namespace MMLib.Alvo.Rules;

/// <summary>
/// Runs a host's write hooks for one stage of one write: validate before any transaction, before-write and after-write
/// inside it. Called by a storage driver at the stages it alone can place; implemented by Alvo's core.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port, as <see cref="IBeforeHookRunner"/> is,</b> because every caller is a driver and the hooks live in the core: one
/// port makes every driver run the same pipeline. Unlike that runner it is asynchronous and takes a token — host code may
/// await calls through the unit's own transaction — and it bounds what it runs with a timeout and an operation budget
/// instead.
/// </para>
/// <para>
/// <b>A refusal is an exception.</b> A rejection rolls the write back with the hook's kind, code and pointer; a hook failure
/// (an exception, a timeout, a budget overrun, a value its field cannot hold, a write to its own row, a registration the
/// current schema does not bind) fails the write and — inside a transaction unit — poisons the unit, so a caller that catches
/// it still cannot commit.
/// </para>
/// </remarks>
public interface IWriteExtensionRunner
{
    /// <summary>Whether any validate hook is registered for <paramref name="operation"/> on <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The write.</param>
    /// <returns><see langword="true"/> when a driver must run the validate stage, with its pre-reads.</returns>
    bool Validates(string entity, AlvoWriteOperation operation);

    /// <summary>Whether any before- or after-stage hook is registered for <paramref name="operation"/> on <paramref name="entity"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="operation">The write.</param>
    /// <returns><see langword="true"/> when the write must run in a transaction unit its hooks can reach.</returns>
    bool RunsInTransaction(string entity, AlvoWriteOperation operation);

    /// <summary>Runs the validate hooks of the write, with no transaction open.</summary>
    /// <param name="invocation">The write; <see cref="WriteInvocation.Transaction"/> is <see langword="null"/>.</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>A task that completes when every hook has passed the write.</returns>
    Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Runs the before-write (or before-delete) hooks of the write, inside its transaction.</summary>
    /// <param name="invocation">The write, with the candidate in <see cref="WriteInvocation.New"/> (a delete: its row in <see cref="WriteInvocation.Old"/>).</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>
    /// The fields the hooks set, keyed by field name — every value already measured against its field's facets; empty when
    /// nothing was set, and always empty for a delete.
    /// </returns>
    Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken);

    /// <summary>Runs the after-write (or after-delete) hooks of the write, inside its transaction, after the row and its rollups.</summary>
    /// <param name="invocation">The write, with the stored row in <see cref="WriteInvocation.Saved"/> (a delete: <see cref="WriteInvocation.Old"/>).</param>
    /// <param name="cancellationToken">The write's token.</param>
    /// <returns>
    /// The id of the last hook that wrote through the transaction — the stored row must then be read again before it is
    /// emitted — or <see langword="null"/> when none wrote.
    /// </returns>
    Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken);
}
