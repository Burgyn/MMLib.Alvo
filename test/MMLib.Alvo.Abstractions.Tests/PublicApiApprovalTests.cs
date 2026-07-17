using PublicApiGenerator;
using System.Reflection;

namespace MMLib.Alvo.Abstractions.Tests;

public class PublicApiApprovalTests
{
    [Fact]
    public Task Public_api_has_not_changed()
    {
        var publicApi = Assembly.Load("MMLib.Alvo.Abstractions").GeneratePublicApi();

        return Verify(publicApi).UseFileName("PublicApi.MMLib.Alvo.Abstractions");
    }
}
