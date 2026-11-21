#load "../_shared/Rows.csx"

using System.Text.Json;

// FIRST, and asserting nothing (README, behaviour 1): Acme's id for every later case, and the code Theo used, which
// 009 presents again to show a used-up invitation answers like every other dead one.
await tp.Test("Capture: Acme's id and the code Theo accepted.", async () =>
{
    async Task<string> Field(string request, string name)
    {
        var body = await BodyOf(tp.Responses[request]);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) ? value.ToString() : "";
    }

    tp.SetVariable("AcmeId", await Field("DanaCreatesAcme", "tenantId"));
    tp.SetVariable("TheoInviteCode", await Field("DanaInvitesTheo", "code"));
});

string[] Roles(JsonElement owner) =>
    owner.GetProperty("roles").EnumerateArray().Select(role => role.GetString()).ToArray();

await tp.Test("A founder owns the tenant she created, as a dispatcher.", async () =>
{
    var acme = await BodyOf(tp.Responses["DanaCreatesAcme"]);

    True(Guid.TryParse(acme.GetProperty("tenantId").GetString(), out _));
    Equal("Acme Repairs", acme.GetProperty("name").GetString());
    Equal("shared", acme.GetProperty("kind").GetString());
    Equal(new[] { "dispatcher" }, Roles(acme));
    True(acme.GetProperty("isOwner").GetBoolean());
});

await tp.Test("An invitation answers its code once, for one person.", async () =>
{
    var invitation = await BodyOf(tp.Responses["DanaInvitesTheo"]);

    True(invitation.GetProperty("code").GetString()?.StartsWith("alvo_inv_", StringComparison.Ordinal) == true, "DanaInvitesTheo's code starts with alvo_inv_");
    Equal(1, invitation.GetProperty("maxUses").GetInt32());
    Equal(0, invitation.GetProperty("uses").GetInt32());
    Equal(new[] { "technician" }, Roles(invitation));
    False(invitation.GetProperty("isOwner").GetBoolean());
    Equal("active", invitation.GetProperty("status").GetString());
});

await tp.Test("Anyone holding the code sees the tenant, the inviter and the roles — and nothing else.", async () =>
{
    var preview = await BodyOf(tp.Responses["AnyonePreviews"]);

    Equal("Acme Repairs", preview.GetProperty("tenantName").GetString());
    Equal(tp.GetVariable<string>("DanaName"), preview.GetProperty("createdBy").GetProperty("name").GetString());
    Equal(new[] { "technician" }, Roles(preview));
    False(preview.GetProperty("isOwner").GetBoolean());

    // The preview names no id: neither the tenant's nor the inviter's, so a leaked code discloses a name, not a key. The
    // inviter is under the key every answer names a creator with, by name alone (#542).
    Equal(
        new[] { "createdBy", "emailBound", "expiresAt", "isOwner", "roles", "tenantName" },
        preview.EnumerateObject().Select(member => member.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    Equal(new[] { "name" }, preview.GetProperty("createdBy").EnumerateObject().Select(member => member.Name).ToArray());
});

await tp.Test("Accepting makes Theo a technician of Acme, and accepting again changes nothing.", async () =>
{
    foreach (var request in new[] { "TheoAccepts", "TheoAcceptsAgain" })
    {
        var membership = await BodyOf(tp.Responses[request]);

        Equal(Constant("AcmeId"), membership.GetProperty("tenantId").GetString());
        Equal("Acme Repairs", membership.GetProperty("name").GetString());
        Equal(new[] { "technician" }, Roles(membership));
        False(membership.GetProperty("isOwner").GetBoolean(), $"{request}: an invitation without isOwner makes no owner");
    }
});

await tp.Test("Acme has exactly two members: Dana, owner and dispatcher; Theo, technician.", async () =>
{
    var members = (await BodyOf(tp.Responses["DanaListsMembers"])).EnumerateArray()
        .ToDictionary(member => member.GetProperty("userId").GetString(), member => member, StringComparer.Ordinal);

    Equal(
        new[] { Constant("DanaId"), Constant("TheoId") }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        members.Keys.OrderBy(id => id, StringComparer.Ordinal).ToArray());

    var dana = members[Constant("DanaId")];
    Equal(new[] { "dispatcher" }, Roles(dana));
    True(dana.GetProperty("isOwner").GetBoolean());
    Equal(tp.GetVariable<string>("DanaName"), dana.GetProperty("name").GetString());

    var theo = members[Constant("TheoId")];
    Equal(new[] { "technician" }, Roles(theo));
    False(theo.GetProperty("isOwner").GetBoolean());
    Equal(tp.GetVariable<string>("TheoName"), theo.GetProperty("name").GetString());
});

await tp.Test("The invitation list never carries a code — on any item.", async () =>
{
    var invitations = (await BodyOf(tp.Responses["DanaListsInvitations"])).EnumerateArray().ToArray();

    // Control: the list is not empty, so "no item has a code" is not satisfied by having no items.
    var theos = Single(invitations);
    var created = await BodyOf(tp.Responses["DanaInvitesTheo"]);
    Equal(created.GetProperty("id").GetString(), theos.GetProperty("id").GetString());
    Equal("usedUp", theos.GetProperty("status").GetString());
    Equal(1, theos.GetProperty("uses").GetInt32());

    foreach (var invitation in invitations)
    {
        DoesNotContain("code", invitation.EnumerateObject().Select(member => member.Name));
    }
});
