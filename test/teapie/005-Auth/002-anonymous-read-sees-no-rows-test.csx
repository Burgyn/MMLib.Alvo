await tp.Test("The page an anonymous caller reads is empty, though the row the credentialed caller created exists.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();

    Contains("\"items\":[]", body.Replace(" ", string.Empty));
    DoesNotContain(tp.GetVariable<string>("OwnerId"), body);
});
