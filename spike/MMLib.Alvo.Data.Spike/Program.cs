using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Data.Spike;

/// <summary>
/// THROWAWAY SPIKE. Answers the eight pass/fail questions that open F3 PR2 — see
/// docs/superpowers/specs/2026-07-26-f3-pr2-spike-verdict.md. Run:
///   dotnet run --project spike/MMLib.Alvo.Data.Spike            (both engines)
///   dotnet run --project spike/MMLib.Alvo.Data.Spike -- sqlite  (SQLite only, no Docker)
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var services = new ServiceCollection();
        services.AddAlvo();
        await using var provider = services.BuildServiceProvider();
        var compiler = provider.GetRequiredService<ICelCompiler>();
        var renderer = provider.GetRequiredService<IPredicateRenderer>();

        var only = args.Length > 0 ? args[0] : null;
        List<SpikeEngine> engines = [];
        if (only is null or "sqlite")
        {
            engines.Add(new SqliteSpikeEngine());
        }

        if (only is null or "postgres" or "pg")
        {
            engines.Add(new PostgresSpikeEngine());
        }

        foreach (var engine in engines)
        {
            try
            {
                await engine.InitializeAsync();
                await new Probes(engine, compiler, renderer).RunAsync();
            }
            catch (Exception exception)
            {
                Console.WriteLine();
                Console.WriteLine($"!!!! {engine.Name} aborted: {exception.GetType().FullName}: {exception.Message}");
                Console.WriteLine(exception.StackTrace);
            }
            finally
            {
                await engine.DisposeAsync();
            }
        }

        return 0;
    }
}
