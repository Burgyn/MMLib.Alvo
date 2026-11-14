using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// CORS on Alvo's own routes (spec §7.3, S2 D3): the framework's own <see cref="ICorsService"/>, evaluated against a
/// policy Alvo holds, from the first endpoint filter of each route — and one <c>OPTIONS</c> catch-all per route group
/// for the preflights.
/// </summary>
/// <remarks>
/// <para>
/// <b>A filter, not <c>RequireCors</c> metadata.</b> Metadata needs <c>UseCors()</c> in the host's pipeline after routing,
/// and an embedded host must get CORS without a pipeline step. The policy is an object, never a registered policy
/// name, so no host policy can collide with it or replace it.
/// </para>
/// <para>
/// <b>First</b>, so a 401, 403 or 429 a later filter answers with carries the headers too: a browser hides from the
/// script any answer without them, and a refusal the client cannot read is one it cannot act on.
/// </para>
/// <para>
/// <b>The preflight catch-all is <c>OPTIONS</c> only and mapped only when origins are configured</b>, so a host with
/// none keeps answering a preflight 405 exactly as before. None of Alvo's own routes accepts a CORS preflight in
/// routing's sense, so a preflight is routed as the plain <c>OPTIONS</c> it is and reaches the catch-all. It is excluded
/// from the OpenAPI document: it is protocol, not an operation.
/// </para>
/// <para>
/// <b>Never credentials</b>, and so never <c>*</c> (refused at startup): every Alvo credential travels in a header the
/// policy names.
/// </para>
/// </remarks>
internal sealed class AlvoCors
{
    private static readonly TimeSpan _preflightMaxAge = TimeSpan.FromMinutes(10);

    private static readonly string[] _methods =
        [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    private readonly ICorsService _cors;

    /// <summary>Builds the policy from the configured origins and header names.</summary>
    /// <param name="api">The API options, for the origins.</param>
    /// <param name="auth">The auth options, for the API-key and tenant header names.</param>
    /// <param name="cors">The framework's CORS evaluator.</param>
    public AlvoCors(IOptions<AlvoApiOptions> api, IOptions<AlvoAuthOptions> auth, ICorsService cors)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(auth);
        _cors = cors ?? throw new ArgumentNullException(nameof(cors));
        Policy = Build(api.Value.Cors?.Origins ?? [], auth.Value);
        Filter = new CorsFilter(this);
    }

    /// <summary>Gets whether any origin is configured — without one, Alvo sends no CORS header at all.</summary>
    internal bool Enabled => Policy.Origins.Count > 0;

    /// <summary>Gets the policy every Alvo route is evaluated against.</summary>
    internal CorsPolicy Policy { get; }

    /// <summary>Gets the filter that applies <see cref="Policy"/> before the handler runs; add it first.</summary>
    internal IEndpointFilter Filter { get; }

    /// <summary>
    /// Maps <c>OPTIONS {prefix}/{**path}</c>, answering a preflight 204 with the evaluated headers — when
    /// <see cref="Enabled"/>; otherwise nothing.
    /// </summary>
    /// <param name="endpoints">The builder the routes are mapped on.</param>
    /// <param name="prefix">The routes' common prefix, normalized (no trailing slash), or empty for the root.</param>
    internal void MapPreflight(IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!Enabled)
        {
            return;
        }

        endpoints.MapMethods($"{prefix}/{{**path}}", [HttpMethods.Options], (HttpContext http) =>
            {
                Apply(http);
                return Results.NoContent();
            })
            .ExcludeFromDescription();
    }

    /// <summary>Evaluates <see cref="Policy"/> for a request carrying <c>Origin</c> and writes what it allows.</summary>
    /// <param name="http">The request's context, before its response has started.</param>
    internal void Apply(HttpContext http)
    {
        if (Enabled && http.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            _cors.ApplyResult(_cors.EvaluatePolicy(http, Policy), http.Response);
        }
    }

    /// <summary>
    /// An origin as a browser sends it in <c>Origin</c> (RFC 6454 §6.1): lower-case scheme and host, the port only when
    /// it is not the scheme's default, no trailing slash.
    /// </summary>
    /// <param name="origin">A configured origin, already validated.</param>
    internal static string Normalize(string origin)
        => new Uri(origin, UriKind.Absolute).GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static CorsPolicy Build(IEnumerable<string> origins, AlvoAuthOptions auth) => new CorsPolicyBuilder()
        .WithOrigins([.. origins.Select(Normalize)])
        .WithMethods(_methods)
        .WithHeaders(
            HeaderNames.Authorization,
            HeaderNames.ContentType,
            auth.HeaderName,
            auth.TenantHeaderName,
            DataApiEndpoints.IdempotencyKeyHeader,
            HeaderNames.IfMatch,
            HeaderNames.IfNoneMatch,
            PreferHeader.Name)
        .WithExposedHeaders(
            HeaderNames.ETag,
            HeaderNames.Location,
            HeaderNames.WWWAuthenticate,
            HeaderNames.RetryAfter,
            PreferHeader.AppliedName)
        .SetPreflightMaxAge(_preflightMaxAge)
        .Build();

    private sealed class CorsFilter(AlvoCors cors) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);
            cors.Apply(context.HttpContext);
            return next(context);
        }
    }
}
