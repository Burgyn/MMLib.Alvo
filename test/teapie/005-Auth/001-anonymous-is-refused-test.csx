await tp.Test("The refusal is Alvo's own problem document, and it names neither the entity nor a row.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();

    Equal("application/problem+json", tp.Response.Content.Headers.ContentType.MediaType);
    Contains("https://alvo.dev/errors/forbidden", body);
    DoesNotContain("owners", body);
});
