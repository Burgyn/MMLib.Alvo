using MMLib.Alvo.DocsGen.Configuration;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Xml;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.DocsGen.Tests.Configuration;

public partial class OutsideOptionsKeysTests
{
    private static readonly string _root = RepositoryRoot.Find();

    [Fact]
    public void Every_entry_is_read_by_its_source() =>
        OutsideOptionsKeys.All.ShouldAllBe(entry =>
            File.ReadAllText(Path.Combine(_root, entry.Source)).Contains($"\"{entry.Key}\"", StringComparison.Ordinal)
            || File.ReadAllText(Path.Combine(_root, entry.Source)).Contains($"\"{OutsideOptionsKeys.FinalSegment(entry.Key)}\"", StringComparison.Ordinal));

    [Fact]
    public void Every_key_literal_in_the_source_is_documented()
    {
        IReadOnlyList<Assembly> assemblies = [.. ShippedAssemblies.All, ShippedAssemblies.Host];
        var documented = OptionsCatalog.Read(assemblies, XmlDocs.Load(assemblies))
            .SelectMany(section => section.Keys.Select(key => key.Key))
            .Concat(OutsideOptionsKeys.All.Select(entry => entry.Key))
            .ToList();

        KeyLiterals().ShouldAllBe(literal => documented.Any(key => key == literal || key.StartsWith(literal + ":", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_connection_string_name_is_alvo()
    {
        var source = File.ReadAllText(Path.Combine(_root, "src", "MMLib.Alvo.Data.Sqlite", "AlvoSqliteBuilderExtensions.cs"));

        source.ShouldContain("DefaultConnectionName");
        source.ShouldContain("\"Alvo\"");
    }

    [Theory]
    [InlineData("Alvo:Admin:CredentialAttemptsPerMinute", "MMLib.Alvo.Host.Internal.AlvoAdminCredentialLimitOptions", "DefaultAttempts")]
    [InlineData("Alvo:Admin:CredentialCeilingPerMinute", "MMLib.Alvo.Host.Internal.AlvoAdminCredentialLimitOptions", "DefaultCeiling")]
    [InlineData("Alvo:Admin:SessionRevalidationSeconds", "MMLib.Alvo.Identity.Internal.AlvoSessionRevalidationOptions", "DefaultSeconds")]
    public void Every_default_is_the_source_constant(string key, string type, string constant)
    {
        var field = ShippedAssemblies.All.Append(ShippedAssemblies.Host)
            .Select(assembly => assembly.GetType(type)).Single(found => found is not null)!
            .GetField(constant, BindingFlags.NonPublic | BindingFlags.Static)!;

        OutsideOptionsKeys.All.Single(entry => entry.Key == key).Default
            .ShouldBe(Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture));
    }

    private static List<string> KeyLiterals() =>
    [
        .. Directory.EnumerateFiles(Path.Combine(_root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => StringLiteral().Matches(File.ReadAllText(path)).Select(match => match.Groups["value"].Value))
            .Where(value => KeyShape().IsMatch(value))
            .Distinct(),
    ];

    [GeneratedRegex("\"(?<value>[^\"\\r\\n]*)\"")]
    private static partial Regex StringLiteral();

    [GeneratedRegex("^Alvo(:[A-Za-z]+){2,}$")]
    private static partial Regex KeyShape();
}
