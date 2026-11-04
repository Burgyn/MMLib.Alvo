using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// One command: mapped over HTTP (<see cref="Pattern"/> set) or added for in-process invocation (<see cref="CommandType"/>
/// set). It shares the extension id namespace (spec 2026-10-14 E3).
/// </summary>
/// <param name="Id">The command's name.</param>
/// <param name="Sequence">Its place among the host's registrations.</param>
/// <param name="Access">Its access rule.</param>
/// <param name="Entity">The entity a mapped command binds its <c>{id}</c> to; null for none.</param>
/// <param name="Pattern">The route pattern of a mapped command; null for an internal one.</param>
/// <param name="CommandType">The <c>IAlvoCommand&lt;TInput&gt;</c> class of an internal command.</param>
/// <param name="InputType">The input type of an internal command.</param>
internal sealed record CommandRegistration(
    string Id, int Sequence, CommandAccess Access, string? Entity, string? Pattern, Type? CommandType, Type? InputType)
    : ExtensionRegistration(Id, Sequence)
{
    /// <summary>The start of every command completion's event type.</summary>
    internal const string EventPrefix = "alvo.command.";

    /// <summary>The end of every command completion's event type.</summary>
    internal const string EventSuffix = ".completed";

    /// <summary>Gets a value indicating whether the command is reached over HTTP.</summary>
    internal bool IsMapped => Pattern is not null;

    /// <summary>Gets the type of the event a completed run emits.</summary>
    internal string EventType => EventPrefix + Id + EventSuffix;

    /// <summary>The completion event's subject: the bound row, or the command.</summary>
    /// <param name="id">The bound row's id, when there is one.</param>
    internal string Subject(Guid? id) => Entity is { } entity && id is { } row ? $"{entity}/{row:D}" : $"commands/{Id}";
}
