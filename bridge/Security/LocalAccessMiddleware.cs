using Microsoft.Extensions.Primitives;

namespace RealmForge.Bridge.Security;

/// <summary>
/// The only gate in front of the API, in this order:
/// 1. Host must be a loopback name (a DNS-rebinding page reaches us under its own host name);
/// 2. Origin, when present, must be http(s)://localhost|127.0.0.1|[::1]:any or "null" - otherwise 403, not just a
///    missing CORS header, so a foreign page cannot trigger actions even without reading the answer;
/// 3. CORS preflight is answered here;
/// 4. /api requests must carry the access token.
/// </summary>
public sealed class LocalAccessMiddleware(RequestDelegate next, BridgeOptions options, AccessToken token)
{
    const string AllowedMethods = "GET, POST";
    static readonly string AllowedHeaders = "Content-Type, " + AccessToken.HeaderName;

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;

        if (!IsLoopbackHost(request.Host))
        {
            await Deny(context, StatusCodes.Status403Forbidden, "Host is not allowed.");
            return;
        }

        string? origin = request.Headers.Origin;
        if (origin is not null)
        {
            if (!IsAllowedOrigin(origin))
            {
                await Deny(context, StatusCodes.Status403Forbidden, "Origin is not allowed.");
                return;
            }
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.AccessControlExposeHeaders = "Location, Retry-After";
        }
        response.Headers.Vary = "Origin";

        if (HttpMethods.IsOptions(request.Method) && request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            response.Headers.AccessControlAllowMethods = AllowedMethods;
            response.Headers.AccessControlAllowHeaders = AllowedHeaders;
            response.Headers.AccessControlMaxAge = "600";
            // Chrome's Private Network Access asks this before letting a page talk to a local server.
            if (request.Headers.TryGetValue("Access-Control-Request-Private-Network", out var pna)
                && StringValues.Equals(pna, "true"))
            {
                response.Headers["Access-Control-Allow-Private-Network"] = "true";
            }
            response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        if (options.RequireToken && request.Path.StartsWithSegments("/api")
            && !token.Matches(request.Headers[AccessToken.HeaderName]))
        {
            await Deny(context, StatusCodes.Status401Unauthorized, $"Missing or wrong {AccessToken.HeaderName} header.");
            return;
        }

        await next(context);
    }

    static bool IsLoopbackHost(HostString host) =>
        host.HasValue && (string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                          || host.Host is "127.0.0.1" or "[::1]");

    bool IsAllowedOrigin(string origin)
    {
        if (origin == "null") return options.AllowNullOrigin;
        // An origin is exactly scheme://host[:port]; Uri.IsLoopback is a name/IP check, no DNS lookup.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && uri.IsLoopback
               && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }

    static Task Deny(HttpContext context, int status, string title) =>
        Results.Problem(statusCode: status, title: title).ExecuteAsync(context);
}
