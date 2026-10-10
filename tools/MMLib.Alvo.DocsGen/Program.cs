using MMLib.Alvo.DocsGen;

var paths = DocsGenPaths.Parse(args, Environment.CurrentDirectory, AppContext.BaseDirectory);
return await DocsGenRun.RunAsync(paths, DocsGenRun.Generators, CancellationToken.None).ConfigureAwait(false);
