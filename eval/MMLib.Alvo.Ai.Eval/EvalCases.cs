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

/// <summary>A graded turn: whether it passed, and why not when it did not.</summary>
/// <param name="Passed">Whether the turn passed.</param>
/// <param name="Why">What the grading saw, when it did not.</param>
internal sealed record Verdict(bool Passed, string Why)
{
    internal static Verdict Pass { get; } = new(true, string.Empty);

    internal static Verdict When(bool passed, string why) => passed ? Pass : new(false, why);
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
    private const int OpeningLength = 240;
    private static readonly string[] _namesAutomation = ["automation", "automatiz"];
    private static readonly string[] _statesLoss = ["lost", "lose", "loss", "discard", "strat", "stratí", "zmaž", "vymaž", "nenávratn"];

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
            turn.HasValidProposal && turn.ChangedPaths.SequenceEqual([$"{Customers}/full_name"]) && turn.RefusedAttempts <= 1,
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] refused={turn.RefusedAttempts}");

    private static Verdict FullNameWithOptionalPart(TurnRecord turn)
    {
        var computed = turn.ProposedText($"{Customers}/full_name/computed") ?? string.Empty;
        return Verdict.When(
            turn.ProposeCalls <= 2 && turn.HasValidProposal && computed.Contains("has(middle_name)", StringComparison.Ordinal),
            $"propose_change={turn.ProposeCalls} valid={turn.HasValidProposal} computed='{computed}'");
    }

    private static Verdict NotesOnBikes(TurnRecord turn)
    {
        var notes = turn.Proposed(BikesNotes);
        return Verdict.When(
            turn.HasValidProposal && turn.ChangedPaths.SequenceEqual([BikesNotes])
                && turn.ProposedText($"{BikesNotes}/type") == "text" && turn.Proposed($"{BikesNotes}/required")?.ToJsonString() != "true",
            $"valid={turn.HasValidProposal} changed=[{Joined(turn.ChangedPaths)}] notes={notes?.ToJsonString()}");
    }

    private static Verdict RenamePhone(TurnRecord turn)
    {
        var renamedFrom = turn.ProposedText($"{Customers}/phone_number/renamedFrom");
        return Verdict.When(
            turn.HasValidProposal && renamedFrom == "phone" && turn.Proposed($"{Customers}/phone") is null && !turn.AnyDestructivePlan,
            $"valid={turn.HasValidProposal} renamedFrom={renamedFrom} destructive={turn.AnyDestructivePlan}");
    }

    private static Verdict TechniciansDeleteParts(TurnRecord turn)
    {
        var rule = turn.ProposedText(PartsDelete) ?? string.Empty;
        return Verdict.When(
            turn.HasValidProposal && rule.Contains("'technician' in @user.roles", StringComparison.Ordinal)
                && turn.ChangedPaths.SequenceEqual([PartsDelete]),
            $"valid={turn.HasValidProposal} rule='{rule}' changed=[{Joined(turn.ChangedPaths)}]");
    }

    private static Verdict AutomationRefused(TurnRecord turn)
    {
        var namesAutomation = _namesAutomation.Any(word => turn.Answer.Contains(word, StringComparison.OrdinalIgnoreCase));
        return Verdict.When(
            turn.Proposal is null && namesAutomation,
            $"proposal={turn.Proposal is not null} namesAutomation={namesAutomation}");
    }

    private static Verdict DropStreet(TurnRecord turn)
    {
        var opening = turn.Answer.Length > OpeningLength ? turn.Answer[..OpeningLength] : turn.Answer;
        var planViolation = turn.HasViolation("source", "plan");
        return Verdict.When(
            planViolation && !turn.HasValidProposal && _statesLoss.Any(word => opening.Contains(word, StringComparison.OrdinalIgnoreCase)),
            $"planViolation={planViolation} valid={turn.HasValidProposal} opening='{opening}'");
    }

    private static string Joined(IEnumerable<string> paths) => string.Join(", ", paths);
}
