using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace SetGameServer.Auth;

/// <summary>Mirrors PyJWT's <c>jwt.InvalidTokenError</c>.</summary>
public sealed class InvalidTokenException : Exception
{
    public InvalidTokenException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>HTTP-statusful auth failure, translated by the endpoint layer.</summary>
public sealed class AuthException : Exception
{
    public int StatusCode { get; }
    public AuthException(int status, string message) : base(message) => StatusCode = status;
}

/// <summary>
/// C# port of the Python <c>require_auth</c> dependency: validate a Bearer
/// token, fall back to anonymous when allowed, otherwise raise an HTTP error.
/// Configuration (env vars or IConfiguration keys):
///   AUTH_ISSUER, AUTH_AUDIENCE, AUTH_METADATA_URI, ALLOW_ANONYMOUS.
/// </summary>
public sealed class JwtAuth
{
    private readonly ILogger<JwtAuth> _logger;
    private readonly string? _issuer;
    private readonly string? _audience;
    private readonly bool _allowAnonymous;
    private readonly JsonWebTokenHandler _handler = new();
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configManager;

    public JwtAuth(ILogger<JwtAuth> logger, IConfiguration config)
    {
        _logger = logger;
        _issuer = config["AUTH_ISSUER"];
        _audience = config["AUTH_AUDIENCE"];
        var metadataUri = config["AUTH_METADATA_URI"];

        var allow = config["ALLOW_ANONYMOUS"];
        _allowAnonymous = string.IsNullOrEmpty(allow)
            || allow.Equals("1", StringComparison.OrdinalIgnoreCase)
            || allow.Equals("true", StringComparison.OrdinalIgnoreCase)
            || allow.Equals("yes", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(metadataUri))
        {
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataUri,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever
                {
                    RequireHttps = metadataUri.StartsWith("https://", StringComparison.Ordinal),
                });
        }
    }

    public async Task<UserInfo> RequireAuthAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader))
        {
            if (_allowAnonymous) return Stash(ctx, UserInfo.Anonymous());
            throw new AuthException(401, "No authorization header provided");
        }

        if (!authHeader.StartsWith("Bearer ", StringComparison.Ordinal))
            throw new AuthException(401, "Invalid authorization header format");

        var token = authHeader["Bearer ".Length..].Trim();

        try
        {
            var claims = await ValidateTokenAsync(token, ct);
            return Stash(ctx, UserInfo.FromClaims(claims));
        }
        catch (InvalidTokenException e)
        {
            if (_allowAnonymous) return Stash(ctx, UserInfo.Anonymous());
            _logger.LogError("Token validation error: {Message}", e.Message);
            throw new AuthException(401, "Invalid token: " + e.Message);
        }
        catch (AuthException) { throw; }
        catch (Exception e)
        {
            if (_allowAnonymous) return Stash(ctx, UserInfo.Anonymous());
            _logger.LogError(e, "Authentication failed");
            throw new AuthException(500, "Authentication failed: " + e.Message);
        }
    }

    private static UserInfo Stash(HttpContext ctx, UserInfo user)
    {
        ctx.Items["current_user"] = user;
        return user;
    }

    private async Task<IReadOnlyDictionary<string, JsonElement>> ValidateTokenAsync(
        string token, CancellationToken ct)
    {
        if (!_handler.CanReadToken(token))
            throw new InvalidTokenException("Token is not a well-formed JWT");

        if (_configManager is null)
        {
            // No JWKS configured — decode-only fallback for local dev.
            try
            {
                return ClaimsToDict(_handler.ReadJsonWebToken(token));
            }
            catch (Exception e)
            {
                throw new InvalidTokenException(e.Message, e);
            }
        }

        OpenIdConnectConfiguration discovery;
        try
        {
            discovery = await _configManager.GetConfigurationAsync(ct);
        }
        catch (Exception e)
        {
            // Discovery / network failures are not "invalid token" — bubble
            // up so the outer handler maps them to 500 (or anonymous).
            throw new Exception("Failed to load JWT signing metadata: " + e.Message, e);
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = !string.IsNullOrEmpty(_issuer),
            ValidIssuer = _issuer,
            ValidateAudience = !string.IsNullOrEmpty(_audience),
            ValidAudience = _audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = discovery.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        };

        var result = await _handler.ValidateTokenAsync(token, parameters);
        if (!result.IsValid)
            throw new InvalidTokenException(
                result.Exception?.Message ?? "Token validation failed",
                result.Exception);

        return ClaimsToDict((JsonWebToken)result.SecurityToken);
    }

    private static Dictionary<string, JsonElement> ClaimsToDict(JsonWebToken jwt)
    {
        var json = string.IsNullOrEmpty(jwt.EncodedPayload)
            ? "{}"
            : System.Text.Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();
        return dict;
    }
}
