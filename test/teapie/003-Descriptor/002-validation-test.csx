await tp.Test("The refusal is an Alvo problem document naming every field at fault.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();

    Equal("application/problem+json", tp.Response.Content.Headers.ContentType.MediaType);
    Contains("https://alvo.dev/errors/validation", body);
    Contains("\"violations\"", body);
    Contains("/name", body);
    Contains("/email", body);
});
