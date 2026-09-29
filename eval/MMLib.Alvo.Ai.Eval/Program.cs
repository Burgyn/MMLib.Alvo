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

return await EvalSession.RunAsync(options, cancel.Token);
