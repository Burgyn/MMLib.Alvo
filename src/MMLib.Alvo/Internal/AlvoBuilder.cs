using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Internal;

/// <summary>The concrete <see cref="IAlvoBuilder"/> created by <c>AddAlvo</c>.</summary>
internal sealed class AlvoBuilder(IServiceCollection services) : IAlvoBuilder
{
    /// <inheritdoc/>
    public IServiceCollection Services { get; } = services;
}
