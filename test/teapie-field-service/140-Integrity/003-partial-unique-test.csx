#load "../_shared/Rows.csx"

await tp.Test("A second open job on the same customer and PO is 409 `unique`.", async () =>
{
    var refused = tp.Responses["PoAgain"];
    Equal(409, (int)refused.StatusCode);
    Equal("https://alvo.dev/errors/conflict", await ProblemType(refused));
    // A composite key is reported once per field in it, so the caller sees the whole pair it collided on.
    Equal(new[] { "unique", "unique" }, await ViolationCodes(refused));
    Equal(new[] { "/customer_id", "/po_number" }, await ViolationPointers(refused));
});

await tp.Test("The key is the pair and the index is partial: three creates on the same PO were stored.", async () =>
{
    // Another customer on the same PO: so the index is not a plain unique on po_number.
    Equal("PO-778", (await BodyOf(tp.Responses["PoOtherCustomer"])).GetProperty("po_number").GetString());

    // Cancelling the first job released the PO for Acme: so the cancelled row is outside the index.
    Equal("cancelled", (await BodyOf(tp.Responses["CancelFirst"])).GetProperty("status").GetString());
    var again = await BodyOf(tp.Responses["PoAfterCancel"]);
    Equal("PO-778", again.GetProperty("po_number").GetString());
    Equal(Constant("CustomerAcmeId"), again.GetProperty("customer_id").GetString());
});
