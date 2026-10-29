using MMLib.Alvo.Auth;
using MMLib.Alvo.Migrations;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// The two admission checks every management member makes — the caller's level for the operation, then the project — and
/// who the caller is, for the audit.
/// </summary>
/// <remarks>
/// <c>AlvoManagementService</c> holds the same two checks privately; they are not folded into this class in this change, so
/// that the surface every existing management fact pins is not touched to serve a second port.
/// </remarks>
internal sealed class ManagementGate(AlvoBootState boot, IAlvoContextAccessor callers, ManagementAccessEvaluator access)
{
    internal AlvoContext Caller => callers.Principal?.Context ?? AlvoContext.Anonymous;

    internal string Actor => callers.Principal is { } principal ? $"{principal.KeyId}:{principal.Context.User.Value}" : "anonymous";

    internal void Admit(ManagementOperation operation, string project)
    {
        if (!access.Allows(operation, Caller))
        {
            throw new ManagementForbiddenException();
        }

        ArgumentNullException.ThrowIfNull(project);
        if (!boot.Projects.ContainsKey(project))
        {
            throw new ManagementProjectNotFoundException(project, [.. boot.Projects.Keys]);
        }
    }
}
