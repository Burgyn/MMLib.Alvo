await tp.Test("The list answers Alvo's envelope, not a bare array.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();

    Contains("\"items\"", body);
    Contains("\"next\"", body);
    Contains(tp.GetVariable<string>("OwnerId"), body);
});
