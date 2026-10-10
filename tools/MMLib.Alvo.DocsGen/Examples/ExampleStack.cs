using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Examples;

internal sealed partial record ExampleStack(string ComposeFile, string? EnvFile, IReadOnlyList<string> SecretVariables)
{
    internal static ExampleStack? Find(string repoRoot, string exampleDirectory)
    {
        var composeFile = $"docker-compose.{Path.GetFileName(exampleDirectory)}.yml";
        var composePath = Path.Combine(repoRoot, composeFile);
        if (!File.Exists(composePath))
        {
            return null;
        }

        var envFiles = Directory.GetFiles(exampleDirectory, "*.env");
        var envFile = envFiles.Length == 1 ? Path.GetRelativePath(repoRoot, envFiles[0]).Replace('\\', '/') : null;
        var secrets = RequiredSecret().Matches(File.ReadAllText(composePath))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new ExampleStack(composeFile, envFile, secrets);
    }

    internal string Command()
    {
        var exports = SecretVariables.Select(name => $"export {name}=\"$(openssl rand -hex 16)\"\n");
        var envFile = EnvFile is null ? string.Empty : $"--env-file {EnvFile} ";
        return string.Concat(exports) + $"docker compose {envFile}-f {ComposeFile} up --build --wait";
    }

    [GeneratedRegex(@"\$\{(?<name>[A-Z0-9_]+_SECRET):\?")]
    private static partial Regex RequiredSecret();
}
