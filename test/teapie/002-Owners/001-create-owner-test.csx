await tp.Test("The created owner carries a server-assigned id and the name we sent.", async () =>
{
    dynamic owner = await tp.Response.GetBodyAsExpandoAsync();

    NotNull(owner.id);
    Equal("TeaPie Ltd", (string)owner.name);

    tp.SetVariable("OwnerLocation", tp.Response.Headers.Location.ToString());
    tp.SetVariable("OwnerId", (string)owner.id);
});

tp.Test("The 201 carries an ETag, so a conditional write is possible without a read first.", () =>
{
    NotNull(tp.Response.Headers.ETag);
});
