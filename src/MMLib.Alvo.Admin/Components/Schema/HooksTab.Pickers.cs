using MMLib.Alvo.Admin.Internal;
using System.Text.Json;

namespace MMLib.Alvo.Admin.Components.Schema;

/* The endpoint and template pickers, the payload box and the recipient (spec §4.4, §4.5; rulings B4, B5). */
public partial class HooksTab
{
    private const string EndpointSelectId = "hook-endpoint";
    private const string TemplateSelectId = "hook-template";

    private List<Choice> _endpoints = [];
    private List<Choice> _templates = [];
    private int _bodyFileTemplates;

    /// <summary>Reads what the pickers offer from the working copy — a declaration not applied yet included (ruling B5).</summary>
    private void ReadPickers()
    {
        if (Copy is not { } copy)
        {
            _endpoints = [];
            _templates = [];
            _bodyFileTemplates = 0;
            return;
        }

        var endpoints = DescriptorLens.Endpoints(copy.Json).Select(pair => pair.Key).ToList();
        _endpoints = Choices(endpoints, Names(DescriptorLens.Endpoints(copy.AppliedJson)), Current.Endpoint, "not declared");
        var templates = DescriptorLens.Templates(copy.Json);
        var bodyFiles = templates.Where(pair => DescriptorLens.HasBodyFile(pair.Value)).Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        _bodyFileTemplates = bodyFiles.Count;
        var offered = templates.Select(pair => pair.Key).Where(name => !bodyFiles.Contains(name)).ToList();
        var missing = bodyFiles.Contains(Current.Template) ? "reads a bodyFile" : "not declared";
        _templates = Choices(offered, Names(DescriptorLens.Templates(copy.AppliedJson)), Current.Template, missing);
    }

    private Task ChooseEndpointAsync(string? name)
    {
        Current.Endpoint = name ?? string.Empty;
        _ = CheckPayloadAsync();
        return RefocusSelectAsync(EndpointSelectId);
    }

    private void TypePayload(string? text)
    {
        Current.Payload = text ?? string.Empty;
        _ = CheckPayloadAsync();
    }

    private Task ChooseTemplateAsync(string? name)
    {
        Current.Template = name ?? string.Empty;
        _ = CheckToAsync();
        return RefocusSelectAsync(TemplateSelectId);
    }

    private void TypeTo(string? text)
    {
        Current.To = text ?? string.Empty;
        _ = CheckToAsync();
    }

    /// <summary>
    /// The payload, judged by the live check at <c>…/action/payload</c> (the B4 deviation: the build's own refusal, not a
    /// copy of its classifier) — once a declared endpoint is chosen, because an undeclared one stops the compiler first.
    /// </summary>
    private Task CheckPayloadAsync() => CheckAsync(
        "hook-payload",
        Current.Kind == HookBuilder.Webhook && EndpointDeclared && Current.Payload.Length <= HookBuilder.MaxPayloadLength
            ? Current.Payload
            : string.Empty,
        (copy, _) => ActionSlot(copy, "action", "payload"));

    /// <summary>The recipient, judged at <c>…/action/to</c> once a declared template is chosen, for the same reason.</summary>
    private Task CheckToAsync() => CheckAsync(
        "hook-to",
        Current.Kind == HookBuilder.Email && TemplateDeclared ? Current.To : string.Empty,
        (copy, _) => ActionSlot(copy, "action", "to"));

    private bool EndpointDeclared => _endpoints.Any(choice => choice.Declared && choice.Name == Current.Endpoint);

    private bool TemplateDeclared => _templates.Any(choice => choice.Declared && choice.Name == Current.Template);

    private string EndpointHint => _endpoints.Count == 0
        ? "No endpoint is declared yet. Declare one on the Integrations screen first — this sheet does not keep what you typed if you leave it."
        : Current.Endpoint.Length > 0 && !EndpointDeclared
            ? $"'{Current.Endpoint}' is not declared; the apply refuses a hook that names it. Choose a declared endpoint, or declare it on the Integrations screen."
            : "An endpoint declared under webhooks.endpoints — the Integrations screen declares one. A declaration not applied yet is offered too.";

    private string TemplateHint
        => (_templates.Count == 0
               ? "No template is declared yet. Declare one on the Integrations screen first — this sheet does not keep what you typed if you leave it."
               : "A template declared under templates — the Integrations screen declares one.")
           + (_bodyFileTemplates > 0
               ? $" Not offered: {_bodyFileTemplates} that read a bodyFile, because this build refuses an email that sends one."
               : string.Empty);

    /// <summary>The payload box's descriptions: its hint, the local sentence while there is one, and the check's.</summary>
    private string PayloadDescribedBy
    {
        get
        {
            var ids = new List<string> { "hook-payload-hint" };
            if (Current.Payload.Length > HookBuilder.MaxPayloadLength)
            {
                ids.Add("hook-payload-length");
            }
            else if (Current.Payload.Length > 0 && !EndpointDeclared)
            {
                ids.Add("hook-payload-pending");
            }

            if (_check.DescribedBy("hook-payload") is { } check)
            {
                ids.Add(check);
            }

            return string.Join(' ', ids);
        }
    }

    /// <summary>The offered names, each saying whether it is applied, and the hook's own value first when nothing offers it.</summary>
    private static List<Choice> Choices(IReadOnlyList<string> offered, HashSet<string> applied, string current, string missing)
    {
        var choices = offered
            .Select(name => new Choice(name, applied.Contains(name) ? name : $"{name} (not applied yet)", Declared: true))
            .ToList();
        if (current.Length > 0 && !offered.Contains(current, StringComparer.Ordinal))
        {
            choices.Insert(0, new Choice(current, $"{current} ({missing})", Declared: false));
        }

        return choices;
    }

    private static HashSet<string> Names(IReadOnlyList<KeyValuePair<string, JsonElement>> declared)
        => declared.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>One picker entry.</summary>
    /// <param name="Name">The declared name, the value written.</param>
    /// <param name="Label">What the option reads.</param>
    /// <param name="Declared">Whether the working copy declares it in a form the apply can send.</param>
    private sealed record Choice(string Name, string Label, bool Declared);
}
