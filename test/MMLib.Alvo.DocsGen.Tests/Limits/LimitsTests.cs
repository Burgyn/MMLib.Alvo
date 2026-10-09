using MMLib.Alvo.Api;
using MMLib.Alvo.Data;
using MMLib.Alvo.DocsGen.CSharp;
using MMLib.Alvo.DocsGen.Limits;
using MMLib.Alvo.DocsGen.Xml;
using System.Globalization;

namespace MMLib.Alvo.DocsGen.Tests.Limits;

public class LimitsTests
{
    private static readonly IReadOnlyList<Limit> _limits = LimitsCatalog.Read(XmlDocs.Load(ShippedAssemblies.All));

    [Fact]
    public void Every_row_reads_a_real_member()
    {
        _limits.Count.ShouldBe(12);
        _limits.ShouldAllBe(limit => limit.Value.Length > 0 && limit.Description.Length > 0);
    }

    [Fact]
    public void The_values_are_the_members()
    {
        _limits.Single(limit => limit.Name == "Default page size").Value.ShouldBe(new AlvoApiOptions().DefaultPageSize.ToString(CultureInfo.InvariantCulture));
        _limits.Single(limit => limit.Name == "Filter terms").Value.ShouldBe(AlvoFilter.MaxTerms.ToString(CultureInfo.InvariantCulture));
        _limits.Single(limit => limit.Name == "Request body").Value.ShouldBe("1048576 bytes (1 MiB)");
    }

    [Fact]
    public void Only_the_api_options_have_a_configuration_key() =>
        _limits.Where(limit => limit.ConfigurationKey is not null).ShouldAllBe(limit => limit.ConfigurationKey!.StartsWith("Alvo:Api:", StringComparison.Ordinal));

    [Fact]
    public void The_page_renders_a_row_exactly() =>
        LimitsGenerator.Render([new Limit("Batch rows", "1000", "Alvo:Api:MaxBatchRows", "Rows per batch.", "AlvoApiOptions.MaxBatchRows")])
            .Content.ShouldContain("| Batch rows | 1000 | `Alvo:Api:MaxBatchRows` | Rows per batch. |\n");
}
