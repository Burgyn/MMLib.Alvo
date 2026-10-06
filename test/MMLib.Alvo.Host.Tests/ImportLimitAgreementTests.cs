using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// <b>The Import box stops where the expression check stops</b>: a descriptor the box takes is one the schema editors'
/// live check still reads (#316).
/// </summary>
/// <remarks>This suite sees both assemblies' internals, so it binds the two constants themselves, not a copy of either.</remarks>
public sealed class ImportLimitAgreementTests
{
    [Fact]
    public void The_import_box_takes_exactly_as_many_characters_as_the_expression_check_reads()
        => ImportLimit.MaxChars.ShouldBe(AlvoManagementService.MaxCheckedDescriptorChars);
}
