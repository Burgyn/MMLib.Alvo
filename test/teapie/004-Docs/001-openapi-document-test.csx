using System.Text.Json;

await tp.Test("The document is OpenAPI 3.1 and its paths are this descriptor's entities, and no others.", async () =>
{
    string body = await tp.Response.Content.ReadAsStringAsync();
    JsonElement document = JsonDocument.Parse(body).RootElement;

    StartsWith("3.1", document.GetProperty("openapi").GetString());

    JsonElement paths = document.GetProperty("paths");

    True(paths.TryGetProperty("/api/owners", out _), "The document declares no /api/owners path.");
    True(paths.TryGetProperty("/api/vehicles", out _), "The document declares no /api/vehicles path.");
    True(paths.TryGetProperty("/api/inspections", out _), "The document declares no /api/inspections path.");
    False(paths.TryGetProperty("/api/warehouses", out _), "The document declares /api/warehouses, which the mounted descriptor does not.");
});
