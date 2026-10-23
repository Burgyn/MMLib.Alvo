using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;

using NSubstitute;

namespace MMLib.Alvo.Tests.Events;

/// <summary>
/// The pump a host resolves drives the dispatcher the host runs — one dispatcher instance, however often
/// <c>AddAlvo</c> is called. Two instances would be two drivers of one outbox.
/// </summary>
public sealed class EventPumpRegistrationTests
{
    [Fact]
    public void The_pump_drives_the_one_hosted_dispatcher()
    {
        var collection = Registered(times: 1);
        using var services = collection.BuildServiceProvider();

        var hosted = HostedDispatchers(collection).ShouldHaveSingleItem().ImplementationFactory!(services);

        services.GetRequiredService<IAlvoEventPump>().ShouldBeOfType<EventPump>().Outbox.ShouldBeSameAs(hosted);
        services.GetRequiredService<OutboxDispatcher>().ShouldBeSameAs(hosted);
    }

    [Fact]
    public void Registering_alvo_twice_still_hosts_one_dispatcher() =>
        HostedDispatchers(Registered(times: 2)).Count.ShouldBe(1);

    private static ServiceCollection Registered(int times)
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(Substitute.For<IOutboxStore>());
        for (var call = 0; call < times; call++)
        {
            collection.AddAlvo();
        }

        return collection;
    }

    private static List<ServiceDescriptor> HostedDispatchers(ServiceCollection collection) =>
        [.. collection.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationFactory?.Method.ReturnType == typeof(OutboxDispatcher))];
}
