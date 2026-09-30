using MMLib.Alvo.Schema;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Ai.Eval;

/// <summary>One thing the operator asks, in both languages, and what makes the turn a pass.</summary>
/// <param name="Name">The case's name, as <c>--case</c> takes it.</param>
/// <param name="English">The request in English.</param>
/// <param name="Slovak">The same request in Slovak.</param>
/// <param name="Grade">
/// What makes a turn a pass for this case, read off outcomes — the proposal, the dry-run violations, the calls. The
/// reply's wording and language are graded on every case besides (<see cref="EvalRunner.Graded"/>).
/// </param>
/// <param name="ForcesStaleRevision">
/// Whether the turn runs under <see cref="InterferingChatClient"/>, which applies another operator's edit mid-turn (D15).
/// </param>
internal sealed record EvalCase(
    string Name, string English, string Slovak, Func<TurnRecord, Verdict> Grade, bool ForcesStaleRevision = false)
{
    internal string Prompt(string language) => language == ReplyLanguage.Slovak ? Slovak : English;
}

/// <summary>A graded turn: whether it passed, and what the grading saw either way.</summary>
/// <param name="Passed">Whether the turn passed.</param>
/// <param name="Why">What the grading saw — kept on a pass too, so a grader that passes too easily can be audited.</param>
internal sealed record Verdict(bool Passed, string Why)
{
    internal static Verdict When(bool passed, string why) => new(passed, why);
}

/// <summary>
/// The suite: the reliability design's seven §4.2 cases plus the first-try design's two (D15, D16), its seven skill
/// cases (§7.5, D36) and the RCA 2 case (§9, D53), each graded on its outcomes — and every turn also on its wording, its language and the skills its
/// proposal needed (D21, D31).
/// </summary>
/// <remarks>
/// Case 6 is a nightly <c>automation</c> request rather than the design's webhook one (plan deviation D6): this build
/// delivers after-hook webhooks, so "no proposal" would grade a correct answer as a failure.
/// </remarks>
internal static partial class EvalCases
{
    private const string StaleRevision = "stale-revision";
    private const string TechniciansNickname = "/entities/technicians/fields/nickname";
    private const string Customers = "/entities/customers/fields";
    private const string BikesNotes = "/entities/bikes/fields/notes";
    private const string PartsDelete = "/entities/parts/rules/delete";
    private const string MiddleName = $"{Customers}/middle_name";
    private const string FullNamePath = $"{Customers}/full_name";
    private const string GrantsTechnician = "'technician'in@user.roles";
    private static readonly string[] _namesAutomation = ["automation", "automatiz"];
    private static readonly string[] _otherStaffRoles = ["'manager'", "'reception'"];
    private static readonly string[] _statesLoss =
    [
        "lose", "lost", "loss", "eras", "destroy", "destruct", "irrevers", "permanent", "discard",
        "strat", "strac", "strác", "nenávrat", "nenavrat",
    ];

    private static readonly string[] _deletes = ["delet", "zmaz", "vymaz", "zmaž", "vymaž"];

    private static readonly string[] _dataWords =
    [
        "data", "value", "row", "record", "content", "information",
        "dát", "údaj", "hodnot", "záznam", "riadk", "obsah", "informáci",
    ];

    internal static IReadOnlyList<EvalCase> All { get; } =
    [
        new("full_name",
            "Add a full_name column to customers: first name, a space, last name.",
            "Pridaj do customers stĺpec full_name = meno + medzera + priezvisko.",
            FullName),
        new("full_name_optional_part",
            "Add an optional middle_name to customers, and a full_name column: first name, the middle name when there is one, last name.",
            "Pridaj do customers nepovinné middle_name a stĺpec full_name = meno, stredné meno ak je, priezvisko.",
            FullNameWithOptionalPart),
        new("bikes_notes",
            "Add optional notes to bikes, as longer text.",
            "Pridaj do bikes nepovinné poznámky notes ako dlhší text.",
            NotesOnBikes),
        new("rename_phone",
            "Rename customers.phone to phone_number.",
            "Premenuj v customers stĺpec phone na phone_number.",
            RenamePhone),
        new("technicians_delete_parts",
            "Only technicians may delete parts.",
            "Diely (parts) môžu mazať iba technici.",
            TechniciansDeleteParts),
        new("automation_refused",
            "Every night at 2:00, mark the rentals that are past due as overdue.",
            "Každú noc o 2:00 označ výpožičky po termíne ako oneskorené.",
            AutomationRefused),
        new("drop_street",
            "Remove the street field from customers.",
            "Odstráň z customers stĺpec street.",
            DropStreet),
        new("audit_entity",
            "I want an audit of customers: when a customer changes, keep its previous version in an audit entity. For now, at least create the schema.",
            "Chcem audit zákazníkov: keď sa zákazník zmení, ulož jeho predchádzajúcu verziu do audit entity. Zatiaľ sprav aspoň schému.",
            AuditEntity),
        new("stale_revision_recovered",
            "Add an optional nickname field to technicians.",
            "Pridaj technikom (technicians) nepovinné pole nickname.",
            StaleRevisionRecovered,
            ForcesStaleRevision: true),
        new("hook_returned_at",
            "When a rental becomes returned, set returned_at to the current time.",
            "Keď sa výpožička (rentals) vráti, nastav returned_at na aktuálny čas.",
            HookReturnedAt),
        new("reject_negative_price",
            "A part's selling price may never be negative.",
            "Predajná cena dielu (parts) nesmie byť nikdy záporná.",
            RejectNegativePrice),
        new("rollup_rentals_count",
            "Customers: how many rentals they have.",
            "Pri zákazníkoch (customers) chcem vidieť, koľko majú výpožičiek.",
            RollupRentalsCount),
        new("unique_part_per_order",
            "A part may appear only once on each service order.",
            "Jeden diel sa smie na jednej zákazke objaviť iba raz.",
            UniquePartPerOrder),
        new("own_orders_only",
            "Technicians may list and read only the service orders assigned to them; admins and managers still see all.",
            "Technici môžu vidieť a čítať iba zákazky (service_orders), ktoré sú pridelené im; admini a manažéri naďalej všetky.",
            OwnOrdersOnly),
        new("function_action_refused",
            "When a service order is ready, run our invoicing function.",
            "Keď je zákazka hotová (ready), spusti našu fakturačnú funkciu.",
            FunctionActionRefused),
        new("can_alvo_call_http",
            "Can Alvo call our ERP's HTTP API when a part's stock changes?",
            "Vie Alvo zavolať HTTP API nášho ERP, keď sa zmení sklad dielu?",
            CanAlvoCallHttp),
        new("task_management_workers",
            "Create tables for task management for workers: a task links to the employee, the customer they may serve and the goods, and each task has a discussion.",
            "Vytvor tabuľky na správu úloh pre pracovníkov: úloha je priradená zamestnancovi, zákazníkovi, ktorého môže obslúžiť, a tovaru, a ku každej úlohe sa dá viesť diskusia.",
            TaskManagementWorkers),
    ];

    private static Verdict FullName(TurnRecord turn) =>
        Verdict.When(
            turn.HasValidProposal && turn.ChangedPaths.SequenceEqual([FullNamePath]) && turn.RefusedAttempts <= 1,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] refused={turn.RefusedAttempts}");

    /// <summary>Both halves of the request: <c>middle_name</c> added and left optional, and a join that guards it.</summary>
    /// <remarks>
    /// The null-rule refusal's own fix offers "make 'middle_name' required"; a model that took it would have a valid
    /// join and a different schema from the one asked for, so the optionality is graded, not assumed.
    /// </remarks>
    private static Verdict FullNameWithOptionalPart(TurnRecord turn)
    {
        var computed = turn.ProposedText($"{FullNamePath}/computed") ?? string.Empty;
        var optional = turn.Proposed(MiddleName) is not null && turn.Proposed($"{MiddleName}/required")?.ToJsonString() != "true";
        return Verdict.When(
            turn.ProposeCalls <= 2 && turn.HasValidProposal && optional
                && turn.ChangedPaths.Order(StringComparer.Ordinal).SequenceEqual([FullNamePath, MiddleName])
                && ReplyText.Compact(computed).Contains("has(middle_name)", StringComparison.Ordinal),
            $"propose_change={turn.ProposeCalls} valid={turn.HasValidProposal} middleOptional={optional} "
            + $"changed=[{Joined(turn.ChangedPaths)}] computed='{computed}'");
    }

    private static Verdict NotesOnBikes(TurnRecord turn)
    {
        var notes = turn.Proposed(BikesNotes);
        return Verdict.When(
            turn.HasValidProposal && turn.ChangedPaths.SequenceEqual([BikesNotes])
                && turn.ProposedText($"{BikesNotes}/type") == "text" && turn.Proposed($"{BikesNotes}/required")?.ToJsonString() != "true",
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] notes={notes?.ToJsonString()}");
    }

    /// <summary>A rename that keeps the data: <c>renamedFrom</c> set, the old member gone, and no attempt destructive.</summary>
    /// <remarks>
    /// "No attempt", deliberately stricter than the proposal: a valid proposal can never carry a destructive plan (the
    /// dry run refuses one), so the check only means something over every attempt — a turn that tried drop + add first
    /// and repaired it afterwards did not know how to rename, and fails.
    /// </remarks>
    private static Verdict RenamePhone(TurnRecord turn)
    {
        var renamedFrom = turn.ProposedText($"{Customers}/phone_number/renamedFrom");
        return Verdict.When(
            turn.HasValidProposal && renamedFrom == "phone" && turn.Proposed($"{Customers}/phone") is null && !turn.AnyDestructivePlan,
            $"valid={turn.HasValidProposal} renamedFrom={renamedFrom} destructive={turn.AnyDestructivePlan}");
    }

    /// <summary>"Only technicians": the rule grants technicians and no other staff role.</summary>
    /// <remarks>
    /// Keeping <c>'admin'</c> is allowed on purpose: an administrator is not staff the request excludes, and a rule
    /// that locked the administrator out of a table would be a surprising reading of "only technicians". Keeping
    /// <c>'manager'</c> or <c>'reception'</c> — appending the technician to today's rule — is the plausible wrong
    /// answer, and fails.
    /// </remarks>
    private static Verdict TechniciansDeleteParts(TurnRecord turn)
    {
        var rule = turn.ProposedText(PartsDelete) ?? string.Empty;
        var compact = ReplyText.Compact(rule);
        var othersKept = _otherStaffRoles.Where(role => compact.Contains(role, StringComparison.Ordinal)).ToList();
        return Verdict.When(
            turn.HasValidProposal && compact.Contains(GrantsTechnician, StringComparison.Ordinal) && othersKept.Count == 0
                && turn.ChangedPaths.SequenceEqual([PartsDelete]),
            $"valid={turn.HasValidProposal} rule='{rule}' otherRolesKept=[{Joined(othersKept)}] changed=[{Joined(turn.ChangedPaths)}]");
    }

    private static Verdict AutomationRefused(TurnRecord turn)
    {
        var namesAutomation = _namesAutomation.Any(word => turn.Answer.Contains(word, StringComparison.OrdinalIgnoreCase));
        return Verdict.When(
            turn.Proposal is null && namesAutomation,
            $"proposal={turn.Proposal is not null} namesAutomation={namesAutomation}");
    }

    /// <summary>Refused as destructive, and the model's own first sentence says data is lost.</summary>
    /// <remarks>
    /// Graded on the model's own prose (<see cref="ReplyText.OwnProse"/>): the quote block it is told to open with, and
    /// any refusal or fix text it repeated, are taken out first — the plan refusal's fix itself says "what data it
    /// loses", so a grader that read the quote would pass a reply that never said it.
    /// </remarks>
    private static Verdict DropStreet(TurnRecord turn)
    {
        var opening = ReplyText.FirstSentence(ReplyText.OwnProse(turn.Answer, turn.FrameworkTexts));
        var planViolation = turn.HasViolation("source", "plan");
        return Verdict.When(
            planViolation && !turn.HasValidProposal && StatesDataLoss(opening),
            $"planViolation={planViolation} valid={turn.HasValidProposal} opening='{opening}'");
    }

    /// <summary>Whether a sentence says data is lost — not merely that something would be deleted.</summary>
    /// <remarks>
    /// A loss word counts on its own. A deletion word counts only beside a word for the data itself: "I can't delete the
    /// street field without your approval" names the change, not its cost. The field's own name is deliberately not a
    /// data word — it is in every reply to this request, the ones that never mention the data included.
    /// </remarks>
    internal static bool StatesDataLoss(string sentence) =>
        ContainsAny(sentence, _statesLoss) || (ContainsAny(sentence, _deletes) && ContainsAny(sentence, _dataWords));

    private static bool ContainsAny(string text, string[] stems) =>
        stems.Any(stem => text.Contains(stem, StringComparison.OrdinalIgnoreCase));

    /// <summary>The #289 transcript: one new entity under a schema-valid name, right on the first attempt (D16).</summary>
    /// <remarks>
    /// Schema only, as asked: exactly one changed path, so a proposal that also adds an after-hook — the unhonoured
    /// <c>entity.update</c> the transcript confused — fails. "Declares no managed column" is implied by a valid dry run
    /// and graded anyway, so a validator that stopped refusing <c>id</c> still fails the case. Whether the entity refers
    /// to customers is printed, not graded: the mechanism that fills it is #288's.
    /// </remarks>
    private static Verdict AuditEntity(TurnRecord turn)
    {
        var entity = NewEntity(turn);
        var declared = entity is null ? null : turn.Proposed(entity);
        var managed = DeclaredManagedColumns(declared);
        return Verdict.When(
            turn.HasValidProposal && turn.RefusedAttempts == 0 && turn.ProposeCalls == 1 && entity is not null && managed.Count == 0,
            AuditWhy(turn, entity, declared, managed));
    }

    private static string AuditWhy(TurnRecord turn, string? entity, JsonNode? declared, List<string> managed) =>
        $"valid={turn.HasValidProposal} refused={turn.RefusedAttempts} propose_change={turn.ProposeCalls} "
        + $"newEntity={entity ?? "none"} managedDeclared=[{Joined(managed)}] refersToCustomers={RefersTo(declared, "customers")} "
        + $"changed=[{Joined(turn.ChangedPaths)}]";

    /// <summary>A stale-revision refusal the world forced, recovered in the same turn by re-reading and re-basing (D15).</summary>
    /// <remarks>
    /// Stricter than "carries both edits": the proposal changes exactly the other operator's description and the new
    /// field, and leaves the field optional, as asked.
    /// </remarks>
    private static Verdict StaleRevisionRecovered(TurnRecord turn)
    {
        var forced = turn.HasViolation("code", StaleRevision);
        var changed = turn.ChangedPaths.Order(StringComparer.Ordinal).ToList();
        return Verdict.When(
            forced && turn.HasValidProposal && turn.ProposeCalls <= 2
                && changed.SequenceEqual([EvalWorld.OtherOperatorsEdit, TechniciansNickname])
                && turn.Proposed($"{TechniciansNickname}/required")?.ToJsonString() != "true",
            $"forced={forced} valid={turn.HasValidProposal} propose_change={turn.ProposeCalls} changed=[{Joined(changed)}]");
    }

    private static string? NewEntity(TurnRecord turn) =>
        turn.ChangedPaths is [var only] && EntityPointer().IsMatch(only)
            && DescriptorDiff.At(JsonNode.Parse(turn.OriginalDescriptor), only) is null ? only : null;

    private static List<string> DeclaredManagedColumns(JsonNode? entity)
    {
        var owned = AlvoManagedColumns.For(
            Text(entity, "tenancy") == "scoped" ? TenancyMode.Scoped : null, Flag(entity, "audit"), Flag(entity, "softDelete"));
        return [.. (entity?["fields"] as JsonObject ?? new JsonObject()).Select(field => field.Key).Where(owned.Contains)];
    }

    private static bool Flag(JsonNode? entity, string trait) => entity?[trait] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static string? Text(JsonNode? entity, string member) =>
        entity?[member] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool RefersTo(JsonNode? entity, string target) =>
        (entity?["fields"] as JsonObject ?? new JsonObject()).Any(field => field.Value?["entity"]?.GetValue<string>() == target);

    private static string Joined(IEnumerable<string> paths) => string.Join(", ", paths);

    [GeneratedRegex("^/entities/[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityPointer();
}
