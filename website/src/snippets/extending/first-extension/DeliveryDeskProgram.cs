using FirstExtension;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;

namespace FirstExtension.Host;

/// <summary>The host's <c>Program.cs</c>, as a method: top-level programs cannot share the snippets' one assembly.</summary>
public static class DeliveryDeskProgram
{
    public static void Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.Configure<AlvoAuthOptions>(builder.Configuration.GetSection("Alvo:Auth"));
        builder.Services.AddHttpClient<IDeliveryAreas, CourierAreas>(courier => courier.BaseAddress = new Uri("https://courier.example/"));

        builder.Services.AddAlvo(alvo => alvo
            .UseSqlite("Data Source=alvo.db")
            .FromDescriptor("delivery-desk.alvo.json")
            .AddDataApi(api => api.RoutePrefix = "/api/alvo")
            .AddDeliveryDesk());

        var app = builder.Build();

        app.MapAlvoHealth();
        app.MapAlvoDataApi();

        app.Run();
    }
}
