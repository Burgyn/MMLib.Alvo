using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using MMLib.Alvo.DocsGen.Xml;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.DocsGen.Tests.Xml;

public class DocIdTests
{
    [Fact]
    public void A_type() => DocId.Of(typeof(AlvoProblemTypes)).ShouldBe("T:MMLib.Alvo.Api.AlvoProblemTypes");

    [Fact]
    public void A_constant() =>
        DocId.Of(typeof(AlvoProblemTypes).GetField(nameof(AlvoProblemTypes.Validation))!)
            .ShouldBe("F:MMLib.Alvo.Api.AlvoProblemTypes.Validation");

    [Fact]
    public void An_extension_method_with_a_generic_and_a_nullable_parameter() =>
        DocId.Of(typeof(AlvoServiceCollectionExtensions).GetMethod(nameof(AlvoServiceCollectionExtensions.AddAlvo))!)
            .ShouldBe("M:Microsoft.Extensions.DependencyInjection.AlvoServiceCollectionExtensions.AddAlvo(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{MMLib.Alvo.IAlvoBuilder})");

    [Fact]
    public void A_params_array() =>
        DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Join))!)
            .ShouldBe("M:MMLib.Alvo.DocsGen.Tests.Xml.DocIdFixture.Join(System.String[])");

    [Fact]
    public void A_by_ref_parameter() =>
        DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.TryRead))!)
            .ShouldBe("M:MMLib.Alvo.DocsGen.Tests.Xml.DocIdFixture.TryRead(System.String,System.Int32@)");

    [Fact]
    public void A_generic_method() =>
        DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Echo))!)
            .ShouldBe("M:MMLib.Alvo.DocsGen.Tests.Xml.DocIdFixture.Echo``1(``0,System.Collections.Generic.IList{``0})");

    [Fact]
    public void A_property_and_a_nested_type()
    {
        DocId.Of(typeof(DocIdFixture).GetProperty(nameof(DocIdFixture.Name))!).ShouldBe("P:MMLib.Alvo.DocsGen.Tests.Xml.DocIdFixture.Name");
        DocId.Of(typeof(DocIdFixture.Nested)).ShouldBe("T:MMLib.Alvo.DocsGen.Tests.Xml.DocIdFixture.Nested");
    }

    [Fact]
    public void Every_shape_resolves_against_the_real_xml()
    {
        var docs = XmlDocs.Load([typeof(AlvoProblemTypes).Assembly, typeof(IAlvoBuilder).Assembly]);
        docs.Has(DocId.Of(typeof(AlvoServiceCollectionExtensions).GetMethod(nameof(AlvoServiceCollectionExtensions.AddAlvo))!)).ShouldBeTrue();
        docs.Has(DocId.Of(typeof(IAlvoManagement).GetMethod(nameof(IAlvoManagement.GetCelFunctionsAsync))!)).ShouldBeTrue();
    }

    [Fact]
    public void Every_fixture_shape_resolves_against_its_own_xml()
    {
        var docs = XmlDocs.Load([typeof(DocIdTests).Assembly]);

        docs.Summary(DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Join))!)).ShouldBe("Joins parts.");
        docs.Has(DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.TryRead))!)).ShouldBeTrue();
        docs.Has(DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Echo))!)).ShouldBeTrue();
        docs.Param(DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Echo))!), "value").ShouldBe("The value.");
        docs.Returns(DocId.Of(typeof(DocIdFixture).GetMethod(nameof(DocIdFixture.Echo))!)).ShouldBe("The same value.");
    }

    [Fact]
    public void A_missing_documentation_file_names_the_assembly() =>
        Should.Throw<FileNotFoundException>(() => XmlDocs.Load([typeof(DocId).Assembly]))
            .Message.ShouldContain("GenerateDocumentationFile");
}
