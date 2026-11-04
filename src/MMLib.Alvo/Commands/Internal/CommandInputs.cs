using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api;
using System.Globalization;
using System.IO.Pipelines;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MMLib.Alvo.Commands.Internal;

/// <summary>
/// Which of a handler's bound arguments describe the request — route values, query, body and uploaded files, not services
/// or the request plumbing — so a key's fingerprint covers what was asked (Ruling 8): minimal-API filters run after binding,
/// so the raw body is already read. An uploaded file is described by its form name, file name, length and the SHA-256 of
/// its content (Ruling 52): two different uploads under one key are a conflict, never a replay.
/// </summary>
internal sealed class CommandInputs(IReadOnlyList<int> positions)
{
    private static readonly HashSet<Type> _supplied =
    [
        typeof(HttpContext), typeof(HttpRequest), typeof(HttpResponse), typeof(CancellationToken), typeof(ClaimsPrincipal),
        typeof(AlvoCommandContext), typeof(AlvoCaller), typeof(Stream), typeof(PipeReader),
    ];

    /// <summary>The fingerprinted parameters of <paramref name="method"/>.</summary>
    internal static CommandInputs Of(MethodInfo method, IServiceProviderIsService services) =>
        new([.. method.GetParameters().Where(parameter => Describes(parameter, services)).Select(parameter => parameter.Position)]);

    /// <summary>
    /// Refuses a handler whose bound input carries an uploaded file below its top level — inside a <c>[FromForm]</c> or
    /// <c>[AsParameters]</c> type, in a list, or in an <see cref="IFormCollection"/> (Ruling 58): only a top-level
    /// <see cref="IFormFile"/> or <see cref="IFormFileCollection"/> parameter is fingerprinted by its content (Ruling 52), and
    /// a file anywhere else would be described by its metadata alone — two different uploads under one key a replay.
    /// </summary>
    /// <exception cref="ArgumentException">A bound parameter carries an uploaded file below its top level.</exception>
    internal static void EnsureUploadsAreTopLevel(string command, Delegate handler, IServiceProviderIsService services)
    {
        var nested = handler.Method.GetParameters()
            .FirstOrDefault(parameter => Describes(parameter, services) && !IsUpload(parameter.ParameterType)
                && CarriesUpload(parameter.ParameterType, services, []));
        if (nested is not null)
        {
            throw new ArgumentException(
                $"Command '{command}' binds '{nested.Name}', a {nested.ParameterType.Name} that carries an uploaded file. An upload is "
                + "fingerprinted by its content only as a top-level IFormFile or IFormFileCollection parameter, so take the file as a "
                + "parameter of its own.",
                nameof(handler));
        }
    }

    /// <summary>The request's fingerprint: the command, the route's <c>id</c>, and each input described, hashed.</summary>
    internal async Task<string> FingerprintAsync(
        CommandRegistration command, HttpContext http, IList<object?> arguments, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var text = new StringBuilder(command.Id).Append('\n').Append(http.Request.RouteValues["id"]);
        foreach (var position in positions)
        {
            text.Append('\n').Append(await DescribedAsync(arguments[position], json, cancellationToken).ConfigureAwait(false));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static async Task<string> DescribedAsync(object? value, JsonSerializerOptions json, CancellationToken cancellationToken) => value switch
    {
        null => "null",
        IFormFile file => await FileAsync(file, cancellationToken).ConfigureAwait(false),
        IFormFileCollection files => await FilesAsync(files, cancellationToken).ConfigureAwait(false),
        _ => JsonSerializer.Serialize(value, value.GetType(), json),
    };

    private static async Task<string> FilesAsync(IFormFileCollection files, CancellationToken cancellationToken)
    {
        var described = new List<string>(files.Count);
        foreach (var file in files)
        {
            described.Add(await FileAsync(file, cancellationToken).ConfigureAwait(false));
        }

        return JsonSerializer.Serialize(described);
    }

    private static async Task<string> FileAsync(IFormFile file, CancellationToken cancellationToken)
    {
        var content = file.OpenReadStream();
        await using (content.ConfigureAwait(false))
        {
            var hash = await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new[] { file.Name, file.FileName, file.Length.ToString(CultureInfo.InvariantCulture), Convert.ToHexStringLower(hash) });
        }
    }

    private static bool IsUpload(Type type) => typeof(IFormFile).IsAssignableFrom(type) || typeof(IFormFileCollection).IsAssignableFrom(type);

    private static bool CarriesUpload(Type type, IServiceProviderIsService services, HashSet<Type> seen)
    {
        if (IsUpload(type))
        {
            return true;
        }

        if (IsOpaque(type, services) || !seen.Add(type))
        {
            return false;
        }

        return ElementOf(type) is { } element && CarriesUpload(element, services, seen)
            || type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => property.GetIndexParameters().Length == 0 && CarriesUpload(property.PropertyType, services, seen));
    }

    private static bool IsOpaque(Type type, IServiceProviderIsService services) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || _supplied.Contains(type) || services.IsService(type);

    private static Type? ElementOf(Type type) =>
        type.IsArray
            ? type.GetElementType()
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                ?.GetGenericArguments()[0];

    private static bool Describes(ParameterInfo parameter, IServiceProviderIsService services) =>
        !_supplied.Contains(parameter.ParameterType)
        && !parameter.GetCustomAttributes().Any(attribute => attribute is IFromServiceMetadata or FromKeyedServicesAttribute)
        && !services.IsService(parameter.ParameterType);
}
