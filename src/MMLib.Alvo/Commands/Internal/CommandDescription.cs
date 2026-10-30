using MMLib.Alvo.Management;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>A command as the extension catalog lists it (Ruling 14).</summary>
internal static class CommandDescription
{
    /// <summary>The method every command is mapped with.</summary>
    internal const string Method = "POST";

    /// <summary>Describes <paramref name="command"/>: a mapped one at the HTTP stage, an internal one before any transaction.</summary>
    /// <param name="command">The command.</param>
    /// <param name="exposeTypeNames">Whether an internal command's class is named.</param>
    internal static ManagementExtension Of(CommandRegistration command, bool exposeTypeNames) => new()
    {
        Id = command.Id,
        Kind = AlvoExtensionKind.Command,
        Stage = command.IsMapped ? AlvoExtensionStage.Http : AlvoExtensionStage.PreTransaction,
        Entities = command.Entity is { } entity ? [entity] : [],
        Route = command.Pattern,
        HttpMethod = command.IsMapped ? Method : null,
        Access = command.Access.Source,
        TypeName = exposeTypeNames ? command.CommandType?.FullName : null,
    };
}
