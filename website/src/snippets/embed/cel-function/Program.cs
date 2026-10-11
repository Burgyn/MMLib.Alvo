using MMLib.Alvo.Auth;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));

builder.Services.AddAlvo(alvo => alvo
    .UseSqlite("Data Source=alvo.db")
    .FromDescriptor("vehicles.alvo.json")
    .AddDataApi(api => api.RoutePrefix = "/api/alvo")
    .AddCelFunction(
        "normalizeVin",
        (string vin) => new string([.. vin.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]),
        "Upper-cases a vehicle identification number and drops every character that is not a letter or a digit."));

var app = builder.Build();

app.MapAlvoHealth();
app.MapAlvoDataApi();

app.Run();
