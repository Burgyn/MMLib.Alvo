using MMLib.Alvo.Extensions;

namespace MMLib.Alvo.Tests.Events;

/// <summary>A handler's concurrency cap: a key the store counts per, and a limit of 1 to 100.</summary>
public sealed class AlvoConcurrencyTests
{
    [Theory]
    [InlineData("tenant", 1)]
    [InlineData("record", 100)]
    public void PerKey_accepts_tenant_and_record(string key, int limit)
    {
        var concurrency = AlvoConcurrency.PerKey(key, limit);

        (concurrency.Key, concurrency.Limit).ShouldBe((key, limit));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("Tenant")]
    [InlineData("")]
    public void PerKey_refuses_another_key(string key) =>
        Should.Throw<ArgumentException>(() => AlvoConcurrency.PerKey(key, 2)).ParamName.ShouldBe("key");

    [Fact]
    public void PerKey_refuses_a_null_key() =>
        Should.Throw<ArgumentNullException>(() => AlvoConcurrency.PerKey(null!, 2));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void PerKey_refuses_a_limit_outside_one_to_one_hundred(int limit) =>
        Should.Throw<ArgumentOutOfRangeException>(() => AlvoConcurrency.PerKey("tenant", limit)).ParamName.ShouldBe("limit");

    [Fact]
    public void Its_text_is_key_colon_limit()
    {
        AlvoConcurrency.PerKey("tenant", 2).ToString().ShouldBe("tenant:2");
        AlvoConcurrency.PerKey("record", 1).ToString().ShouldBe("record:1");
    }
}
