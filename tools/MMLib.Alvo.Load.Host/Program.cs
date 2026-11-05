using MMLib.Alvo.Host;
using MMLib.Alvo.Load.Host;

var builder = AlvoHost.CreateBuilder(args);
builder.Services.AddAlvo(alvo => alvo.AddCalibrationHooks());

await using var app = await AlvoHost.BuildAsync(builder);
await app.RunAsync();
