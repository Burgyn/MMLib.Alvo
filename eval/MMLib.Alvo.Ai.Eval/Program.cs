using MMLib.Alvo.Ai.Eval;

EvalOptions options;
try
{
    options = EvalOptions.Parse(args);
}
catch (ArgumentException usage)
{
    await Console.Error.WriteLineAsync($"[eval-assistant] {usage.Message} (try scripts/eval-assistant --help)");
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    cancel.Cancel();
};

await using var world = await EvalWorld.StartAsync(options.RepositoryRoot, cancel.Token);
var runs = await new EvalRunner(world, options).RunAsync(cancel.Token);
EvalReport.Print(options, runs, Console.Out);

return EvalReport.SuitePasses(runs) ? 0 : 1;
