using System.Text.Json;
using System.Text.Json.Serialization;

namespace SetGameServer.Auth;

public sealed class UserInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; init; }

    [JsonPropertyName("anonymous")]
    public bool IsAnonymous { get; init; }

    [JsonPropertyName("claims")]
    public IReadOnlyDictionary<string, JsonElement>? Claims { get; init; }

    public static UserInfo Anonymous() => new()
    {
        Id = "anonymous",
        Name = "Anonymous",
        IsAnonymous = true,
    };

    public static UserInfo FromClaims(IReadOnlyDictionary<string, JsonElement> claims)
    {
        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (claims.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }

        return new UserInfo
        {
            Id = Get("oid", "sub", "uid", "user_id") ?? "",
            Name = Get("name", "preferred_username", "upn", "unique_name", "email") ?? "",
            Email = Get("email", "preferred_username", "upn"),
            TenantId = Get("tid", "tenant_id"),
            Claims = claims,
        };
    }
}
