using MMLib.Alvo.Host;

var app = await AlvoHost.BuildAsync(AlvoHost.CreateBuilder(args));
await app.RunAsync();
