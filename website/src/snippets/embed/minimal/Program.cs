using MMLib.Alvo.Auth;

var builder = WebApplication.CreateBuilder(args);

// Alvo's own API keys, from configuration (Alvo:Auth).
builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));

builder.Services.AddAlvo(alvo => alvo
    .UseSqlite("Data Source=alvo.db")
    .FromDescriptor("vehicles.alvo.json")
    .AddDataApi(api => api.RoutePrefix = "/api/alvo"));

var app = builder.Build();

app.MapAlvoHealth();
app.MapAlvoDataApi();

app.Run();
