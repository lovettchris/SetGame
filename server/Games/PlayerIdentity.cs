namespace SetGameServer.Games;

/// <summary>
/// Player identity supplied by the hosting environment via well-known headers.
/// In MSRHub the auth proxy injects <c>X-MS-CLIENT-PRINCIPAL-NAME</c> and
/// <c>X-MS-CLIENT-PRINCIPAL-ID</c>. Outside that environment we fall back to a
/// stable cookie-pinned anonymous id so local dev still works.
/// </summary>
public static class PlayerIdentity
{
    private const string CookieName = "setgame_pid";

    public record Identity(string Id, string Name);

    public static Identity From(HttpContext ctx)
    {
        // Highest priority: explicit identity supplied by the SPA (e.g.
        // resolved via the MSRHub gatekeeper). Values are URL-encoded so
        // names with non-ASCII characters survive the header round-trip.
        var spaId = ctx.Request.Headers["X-SetGame-Player-Id"].FirstOrDefault();
        var spaName = ctx.Request.Headers["X-SetGame-Player-Name"].FirstOrDefault();
        if (!string.IsNullOrEmpty(spaId))
        {
            var decId = Uri.UnescapeDataString(spaId);
            var decName = string.IsNullOrEmpty(spaName) ? decId : Uri.UnescapeDataString(spaName);
            return new Identity(decId, decName);
        }

        var id = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"].FirstOrDefault();
        var name = ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-NAME"].FirstOrDefault()
                   ?? ctx.Request.Headers["X-MS-CLIENT-PRINCIPAL-IDP-CLAIM-NAME"].FirstOrDefault();

        if (!string.IsNullOrEmpty(id))
        {
            return new Identity(id, string.IsNullOrEmpty(name) ? id : name);            
        }
        // Local-dev fallback: pin an anonymous id in a cookie.
        var pinned = ctx.Request.Cookies[CookieName];
        if (string.IsNullOrEmpty(pinned))
        {
            pinned = "anon-" + Guid.NewGuid().ToString("N")[..8];
            ctx.Response.Cookies.Append(CookieName, pinned, new CookieOptions
            {
                HttpOnly = false,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });
        }
        return new Identity(pinned, name ?? $"Guest {pinned[5..]}");
    }
}
