using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo;

/// <summary>Builder interface for configuring Alvo.</summary>
public interface IAlvoBuilder
{
    /// <summary>Gets the dependency injection service collection.</summary>
    IServiceCollection Services { get; }
}
