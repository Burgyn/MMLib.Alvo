#load "../../../_shared/Rows.csx"

await tp.Test("An authenticated member reads the whole directory, including the row it cannot write.", async () =>
{
    // Read access and write access are separate questions. `people.list` names only `authenticated`,
    // so the member sees both rows — and the 404 further down proves it still cannot edit one of them.
    // A suite that only checked the refusals could not tell "cannot write" from "cannot see".
    Equal(
        new[] { RunToken() + "-auth Member Profile", RunToken() + "-auth Other Profile" },
        await PageColumn(tp.Responses["MemberReadsDirectory"], "name"));
});

await tp.Test("A create the rule refuses is 403, and it says the POLICY rejected the row.", async () =>
{
    var refused = tp.Responses["MemberCannotCreatePerson"];

    Equal("https://alvo.dev/errors/forbidden", await ProblemType(refused));

    // The wording matters, and it is the one place two different 403s are distinguishable. This is a
    // CONFIGURED rule whose check rejected the candidate row. An operation with no rule at all answers
    // "No policy allows '<op>' on this entity." instead — a different problem with a different fix
    // (write the rule, rather than change it).
    var body = await BodyOf(refused);
    Equal("The write was rejected by policy.", body.GetProperty("detail").GetString());
});

await tp.Test("`||` really is a disjunction: the member is no admin, but it owns this row.", async () =>
{
    var edited = await BodyOf(tp.Responses["MemberEditsOwnProfile"]);

    Equal(RunToken() + "-auth Member Renamed Themselves", edited.GetProperty("name").GetString());

    // And the link that made it work is the field the rule reads. If `user_id` were null here the
    // comparison would collapse to false and this would have been a 404 like the next case.
    Equal(Constant("userMember"), edited.GetProperty("user_id").GetString());
});

await tp.Test("Editing a row you do not own is 404 — not 403 — and the admin's 200 proves it is about you.", async () =>
{
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["MemberCannotEditAnother"]));

    // The control. Same row, same field, different caller: 200. Without it, "there is no such row"
    // explains the 404 just as well, and the test would pass on a build that had lost the row entirely.
    var byAdmin = await BodyOf(tp.Responses["AdminEditsAnotherProfile"]);
    Equal(RunToken() + "-auth Other Profile Edited By Admin", byAdmin.GetProperty("name").GetString());
});

await tp.Test("An ordinary member may file a task: the rules distinguish administering from working.", async () =>
{
    var filed = await BodyOf(tp.Responses["MemberCreatesTask"]);

    Equal(RunToken() + " m filed by a member", filed.GetProperty("title").GetString());

    // `created_by` records WHO, and it is the member rather than the admin who owns the stack's other
    // key — so the two identities really are distinct and the suite is not testing one key twice.
    Equal(Constant("userMember"), filed.GetProperty("created_by").GetString());
});

await tp.Test("THE FINDING: one rule text, two statuses — 403 on create, 404 on delete.", async () =>
{
    // `people.create` and `tasks.delete` are both exactly `'admin' in @user.roles`, and the same
    // member gets a different status from each. It is not an inconsistency:
    //
    //   create — there is no existing row, so the rule is checked against the CANDIDATE row and
    //            rejects it. A refusal is the only answer available. 403.
    //   delete — the row is looked up UNDER the rule as a predicate first. For a non-admin the
    //            predicate matches nothing, so there is no row to delete. 404.
    //
    // Which means: a rule cannot be relied on to produce 403. Only four things do — an unknown
    // entity, the tenant guard, an operation with NO rule, and a rule reading @user.id/@tenant.id
    // that the caller cannot supply. Everything else your predicate excludes is 404, or an empty page
    // on a list. This is the assumption most likely to be wrong in code written against this API.
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["MemberCannotCreatePerson"]));
    Equal("https://alvo.dev/errors/not-found", await ProblemType(tp.Responses["MemberCannotDeleteTask"]));

    // And the row really was there: the admin deleted it immediately afterwards.
    Equal(204, (int)tp.Responses["AdminDeletesTask"].StatusCode);
});

await tp.Test("The create-shaped 403 is the rule's shape, not something about one entity.", async () =>
    Equal("https://alvo.dev/errors/forbidden", await ProblemType(tp.Responses["MemberCannotCreateMilestone"])));

await tp.Test("Every refused write left nothing behind: the directory is the two rows the admin made.", async () =>
    Equal(
        new[] { RunToken() + "-auth Member Renamed Themselves", RunToken() + "-auth Other Profile Edited By Admin" },
        await PageColumn(tp.Responses["DirectoryAfter"], "name")));
