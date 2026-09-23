using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Ai;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A world whose assistant answers from a script, and whose connection is always configured.
/// </summary>
/// <remarks>
/// <b>The one service this suite stands in for.</b> Everything else — the host, the dashboard, the
/// Management API, the database, the apply — is the shipped one, so what these scenarios measure is the
/// dashboard's own behaviour around a proposal rather than a model's mood. A scenario that dialled a real
/// endpoint would be a scenario that fails when somebody's laptop is offline.
/// </remarks>
public sealed class AssistantWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        services.AddSingleton<IAiConnectionResolver, AlwaysConfigured>();
        services.AddSingleton<IAlvoAssistant, ScriptedAssistant>();
    }
}

/// <summary>
/// A world with an agent installed, a writable secret store, and no connection saved yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>The connection resolver is the real one</b>, unlike <see cref="AssistantWorld"/>'s. That is the point:
/// the question this world exists to ask is whether saving a connection through the shipped write path makes
/// the shipped read path report one, and a substituted resolver would answer it by construction.
/// </para>
/// <para>
/// The key file is what makes the store writable, so the settings panel draws a save at all. Thirty-two bytes
/// of base64, generated per world, exactly as a deployment mounts one.
/// </para>
/// </remarks>
public sealed class ConfigurableAssistantWorld : AdminWorld
{
    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services) =>
        services.AddSingleton<IAlvoAssistant, ScriptedAssistant>();

    /// <inheritdoc/>
    protected override void Configure(IDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var key = Path.Combine(Root, "secret.key");
        File.WriteAllText(
            key, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));

        settings["Alvo:Secrets:EncryptionKeyFile"] = key;
    }
}

/// <summary>A connection that always resolves, so the drawer mounts.</summary>
/// <remarks>
/// The address is never dialled: <see cref="ScriptedAssistant"/> answers without a client. It is a loopback
/// port nothing listens on precisely so that a scenario which somehow did dial would fail loudly rather than
/// reach whatever is running on the machine.
/// </remarks>
internal sealed class AlwaysConfigured : IAiConnectionResolver
{
    public ValueTask<AiConnectionResolution> ResolveAsync(CancellationToken ct = default) =>
        new(new AiConnectionResolution(
            new AlvoAiConnection(
                AiConnectionKind.OpenAiCompatible, new Uri("http://127.0.0.1:1/v1"), "scripted", null),
            AiConnectionSource.Store));
}

/// <summary>
/// An assistant that answers by keyword, so a scenario can ask for the turn it wants to measure.
/// </summary>
/// <remarks>
/// Keyed on what the operator types rather than on a queue, because these scenarios ask one question each
/// and a queue would make them order-dependent — which is the failure mode this suite has already paid for
/// once.
/// </remarks>
internal sealed class ScriptedAssistant : IAlvoAssistant
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<AssistantUpdate> AskAsync(
        AssistantRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        yield return new AssistantUpdate.ToolInvoked("get_descriptor");
        yield return new AssistantUpdate.ToolInvoked("validate_descriptor");

        if (request.Message.Contains("drop", StringComparison.OrdinalIgnoreCase))
        {
            yield return new AssistantUpdate.Text("That change is refused.");
            yield return new AssistantUpdate.Proposal(
                DescriptorWith("invoices"), Revision, "Drops a column.", [RefusalText]);
        }
        else
        {
            yield return new AssistantUpdate.Text("Adds an invoices entity with one column.");
            yield return new AssistantUpdate.Proposal(
                DescriptorWith("invoices"), Revision, "Adds an invoices entity.", []);
        }

        await Task.CompletedTask;
    }

    /// <summary>The refusal a scenario asserts is rendered verbatim.</summary>
    internal const string RefusalText =
        "/entities/regions/fields/code: dropping a column discards every value in it.";

    /// <summary>The revision the field-service descriptor is at when this suite starts.</summary>
    private const int Revision = 1;

    /// <summary>
    /// The field-service descriptor with one entity added.
    /// </summary>
    /// <remarks>
    /// Read from the repository rather than written here, for <see cref="Descriptors"/>' reason: a
    /// descriptor written for this suite would be one nothing else uses.
    /// </remarks>
    private static string DescriptorWith(string entity)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(Descriptors.FieldService)!;
        document["entities"]![entity] = System.Text.Json.Nodes.JsonNode.Parse(
            """
            {
              "fields": { "name": { "type": "string", "maxLength": 120, "required": true } },
              "rules": { "list": "'admin' in @user.roles", "get": "'admin' in @user.roles",
                         "create": "'admin' in @user.roles" }
            }
            """);

        return document.ToJsonString();
    }
}
