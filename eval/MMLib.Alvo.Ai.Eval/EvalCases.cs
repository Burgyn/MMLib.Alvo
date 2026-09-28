namespace MMLib.Alvo.Ai.Eval;

/// <summary>One thing the operator asks, in both languages, and what makes the turn a pass.</summary>
/// <param name="Name">The case's name, as <c>--case</c> takes it.</param>
/// <param name="English">The request in English.</param>
/// <param name="Slovak">The same request in Slovak.</param>
/// <param name="Grade">What makes a turn a pass, read off outcomes rather than prose.</param>
internal sealed record EvalCase(string Name, string English, string Slovak, Func<TurnRecord, Verdict> Grade)
{
    internal string Prompt(string language) => language == "sk" ? Slovak : English;
}

/// <summary>A graded turn: whether it passed, and what the grading saw either way.</summary>
/// <param name="Passed">Whether the turn passed.</param>
/// <param name="Why">What the grading saw — kept on a pass too, so a grader that passes too easily can be audited.</param>
internal sealed record Verdict(bool Passed, string Why)
{
    internal static Verdict When(bool passed, string why) => new(passed, why);
}

/// <summary>The suite: the reliability design's §4.2 cases, graded on outcomes rather than prose.</summary>
/// <remarks>
/// Case 6 is a nightly <c>automation</c> request rather than the design's webhook one (plan deviation D6): this build
/// delivers after-hook webhooks, so "no proposal" would grade a correct answer as a failure.
/// </remarks>
internal static class EvalCases
{
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
        "lose", "lost", "loss", "delet", "eras", "destroy", "irrevers", "permanent", "discard",
        "strat", "strac", "strác", "vymaz", "zmaz", "vymaž", "zmaž", "nenávrat", "nenavrat",
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
            planViolation && !turn.HasValidProposal && _statesLoss.Any(word => opening.Contains(word, StringComparison.OrdinalIgnoreCase)),
            $"planViolation={planViolation} valid={turn.HasValidProposal} opening='{opening}'");
    }

    private static string Joined(IEnumerable<string> paths) => string.Join(", ", paths);
}
