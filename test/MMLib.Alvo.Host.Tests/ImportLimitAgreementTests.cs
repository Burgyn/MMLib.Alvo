using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management.Internal;
using System.Reflection;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>The Import box stops where the expression check stops</b>: a descriptor the box takes is one the schema editors'
/// live check still reads (#316).
/// </summary>
/// <remarks>
/// The check's ceiling is a private constant of the core, which the dashboard does not reference; this suite sees both
/// assemblies, so it reads the constant itself rather than a copy of its value.
/// </remarks>
public sealed class ImportLimitAgreementTests
{
    [Fact]
    public void The_import_box_takes_exactly_as_many_characters_as_the_expression_check_reads()
    {
        var ceiling = typeof(AlvoManagementService)
            .GetField("MaxCheckedDescriptorChars", BindingFlags.NonPublic | BindingFlags.Static)
            .ShouldNotBeNull("the check's descriptor ceiling was renamed; point this fact at it again")
            .GetRawConstantValue();

        ceiling.ShouldBe(ImportLimit.MaxChars);
    }
}
