using System.Net;
using System.Text;
using System.Text.Json;
using SetGameServer.Engine;
using SetGameServer.Games;
using SetGameServer.GarnetIO;

var builder = WebApplication.CreateBuilder(args);


DnsEndPoint GetGarnetEndPoint()
{
    int port = 6379;
    if (int.TryParse(Environment.GetEnvironmentVariable("GARNET_PORT"), out int p)){ 
        port = p; 
    }

    var host = Environment.GetEnvironmentVariable("GARNET_HOST") ?? "localhost";

    // If we are given a full URL, strip it down to just the host name (and port if present).
    if (Uri.TryCreate(host, UriKind.Absolute, out Uri? result))
    {
        var hostPart = result.Host;
        var portPart = result.IsDefaultPort ? port : result.Port;
        return new DnsEndPoint(hostPart, portPart);
    }

    return new DnsEndPoint(host, port);
}

var garnetHost = GetGarnetEndPoint();

builder.Services.AddSingleton(sp => new GarnetGameStore(garnetHost.Host, garnetHost.Port,
    sp.GetRequiredService<ILogger<GarnetGameStore>>()));
builder.Services.AddSingleton(sp => new GarnetSubscriber(garnetHost.Host, garnetHost.Port,
    sp.GetRequiredService<ILogger<GarnetSubscriber>>()));

// Cosmos DB durable backup (optional — runs as fire-and-forget behind Garnet).
var cosmosEndpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");
var cosmosDb = Environment.GetEnvironmentVariable("COSMOS_DATABASE") ?? "setgame";
var cosmosManagedId = Environment.GetEnvironmentVariable("COSMOS_MANAGED_IDENTITY_CLIENT_ID");
if (!string.IsNullOrEmpty(cosmosEndpoint))
{
    builder.Services.AddSingleton(sp => new CosmosStore(cosmosEndpoint, cosmosDb, cosmosManagedId,
        sp.GetRequiredService<ILogger<CosmosStore>>()));
    builder.Services.AddSingleton<CosmosBackupQueue>();
    builder.Services.AddSingleton<IBackupQueue>(sp => sp.GetRequiredService<CosmosBackupQueue>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<CosmosBackupQueue>());
}
else
{
    builder.Services.AddSingleton<IBackupQueue>(new NullBackupQueue());
}

builder.Services.AddSingleton<GameService>();

var app = builder.Build();

// Initialize Garnet connections at startup, with a brief retry loop so the
// server can start alongside the Garnet container without a hard race.
{
    var store = app.Services.GetRequiredService<GarnetGameStore>();
    var sub = app.Services.GetRequiredService<GarnetSubscriber>();
    var log = app.Services.GetRequiredService<ILogger<Program>>();

    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (true)
    {
        try
        {
            await store.ConnectAsync();
            await sub.StartAsync();
            log.LogInformation("Connected to Garnet at {host}:{port}", garnetHost.Host, garnetHost.Port);
            break;
        }
        catch (Exception ex) when (DateTime.UtcNow < deadline)
        {
            log.LogWarning("Garnet not ready ({err}); retrying...", ex.Message);
            await Task.Delay(1000);
        }
    }

    app.Lifetime.ApplicationStopping.Register(async () =>
    {
        await store.DisposeAsync();
        await sub.DisposeAsync();
    });

    // Initialize Cosmos DB if configured.
    var cosmos = app.Services.GetService<CosmosStore>();
    if (cosmos != null)
    {
        await cosmos.InitializeAsync();
        log.LogInformation("Cosmos DB backup enabled");

        app.Lifetime.ApplicationStopping.Register(async () =>
        {
            await cosmos.DisposeAsync();
        });
    }
}

var gitCommitHash = Environment.GetEnvironmentVariable("GIT_COMMIT_HASH") ?? "dev";

app.MapGet("/api/version", () => Results.Json(new { commitHash = gitCommitHash }));

app.MapGet("/api/games", async (GameService svc) =>
    Results.Json(await svc.ListAsync()));

app.MapGet("/api/leaderboard", async (GameService svc) =>
    Results.Json(await svc.GetLeaderboardAsync()));

app.MapPost("/api/games", async (CreateRequest body, GameService svc) =>
{
    var name = (body?.Name ?? "").Trim();
    if (string.IsNullOrEmpty(name)) return Results.BadRequest(new { error = "name is required" });
    var summary = await svc.CreateAsync(name);
    return Results.Json(summary);
});

app.MapGet("/api/games/{id}", async (string id, GameService svc) =>
{
    var s = await svc.GetAsync(id);
    return s == null ? Results.NotFound() : Results.Json(s);
});

app.MapPost("/api/games/{id}/join", async (string id, HttpContext ctx, GameService svc) =>
{
    var me = PlayerIdentity.From(ctx);
    var s = await svc.JoinAsync(id, me.Id, me.Name);
    return s == null ? Results.NotFound() : Results.Json(s);
});

app.MapPost("/api/games/{id}/leave", async (string id, HttpContext ctx, GameService svc) =>
{
    var me = PlayerIdentity.From(ctx);
    await svc.LeaveAsync(id, me.Id);
    return Results.Ok();
});

app.MapPost("/api/games/{id}/select", async (string id, SelectRequest body, HttpContext ctx, GameService svc) =>
{
    var me = PlayerIdentity.From(ctx);
    var (state, outcome) = await svc.SubmitAsync(id, me.Id,
        body?.Indices ?? Array.Empty<int>());
    return state == null ? Results.NotFound() : Results.Json(new { state, outcome });
});

// Lightweight endpoint clients hit periodically to measure round-trip
// time. Returns immediately so the RTT reflects network latency only.
app.MapGet("/api/ping", () => Results.Ok(new { ok = true }));

// Records a player's most recently measured RTT for a game so the
// server can size the race-fairness window to match the slowest
// active connection.
app.MapPost("/api/games/{id}/ping", async (string id, PingRequest body, HttpContext ctx, GameService svc) =>
{
    var me = PlayerIdentity.From(ctx);
    await svc.RecordPingAsync(id, me.Id, body?.PingMs ?? 0);
    return Results.Ok();
});

app.MapPost("/api/games/{id}/hint", async (string id, HintRequest body, HttpContext ctx, GameService svc) =>
{
    var me = PlayerIdentity.From(ctx);
    var (state, outcome) = await svc.HintAsync(id, me.Id, body?.Selection ?? Array.Empty<int>());
    return state == null ? Results.NotFound() : Results.Json(new { state, outcome });
});

app.MapPost("/api/games/{id}/deal3", async (string id, GameService svc) =>
{
    var (state, outcome) = await svc.DealAsync(id);
    return state == null ? Results.NotFound() : Results.Json(new { state, outcome });
});

app.MapPost("/api/games/{id}/restart", async (string id, GameService svc) =>
{
    var s = await svc.NewRoundAsync(id);
    return s == null ? Results.NotFound() : Results.Json(s);
});

// Replay export: returns the initial deck shuffle plus every accepted
// move so a finished game can be downloaded as a self-contained JSON
// record. Available at any point in the game (not just after Game Over).
app.MapGet("/api/games/{id}/export", async (string id, GameService svc) =>
{
    var s = await svc.GetAsync(id);
    if (s == null) return Results.NotFound();
    return Results.Json(new
    {
        id = s.Id,
        name = s.Name,
        status = s.Status,
        startedAt = s.StartedAt,
        exportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        initialDeck = s.InitialDeck,
        players = s.Players.Values.Select(p => new
        {
            id = p.Id,
            name = p.Name,
            setsFound = p.SetsFound,
        }),
        moves = s.Moves,
    });
});

// List all persisted replays (id, name, startedAt, playerCount), newest first.
app.MapGet("/api/replays", async (GameService svc) =>
    Results.Json(await svc.GetReplaysAsync()));

// Returns the persisted replay snapshot for a game. Falls back to a
// live export for in-progress or legacy games.
app.MapGet("/api/games/{id}/replay", async (string id, GameService svc) =>
{
    var json = await svc.GetReplayJsonAsync(id);
    return json == null ? Results.NotFound() : Results.Content(json, "application/json");
});

// Server-Sent Events: stream pub/sub messages for one game to one client.
app.MapGet("/api/games/{id}/events", async (string id, HttpContext ctx,
    GameService svc, GarnetSubscriber sub) =>
{
    var initial = await svc.GetAsync(id);
    if (initial == null) { ctx.Response.StatusCode = 404; return; }

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";

    var json = JsonSerializer.Serialize(initial, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    await WriteSseAsync(ctx.Response, "state", json);

    await using var subscription = await sub.SubscribeAsync(GameService.Channel(id));
    using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(20));

    var heartbeatTask = Task.Run(async () =>
    {
        try
        {
            while (await heartbeat.WaitForNextTickAsync(ctx.RequestAborted))
                await ctx.Response.WriteAsync(": keep-alive\n\n", ctx.RequestAborted);
        }
        catch (OperationCanceledException) { }
    });

    try
    {
        await foreach (var payload in subscription.Listener.Reader.ReadAllAsync(ctx.RequestAborted))
            await WriteSseAsync(ctx.Response, "state", payload);
    }
    catch (OperationCanceledException) { }
    finally { try { await heartbeatTask; } catch { } }
});

// Static SPA served from wwwroot (the ASP.NET Core convention). The
// /api/* routes above take precedence; anything else falls through to
// index.html and the SPA assets in wwwroot/.
app.UseDefaultFiles();
app.UseStaticFiles();

app.Run();

static async Task WriteSseAsync(HttpResponse res, string evt, string data)
{
    var sb = new StringBuilder();
    sb.Append("event: ").Append(evt).Append('\n');
    foreach (var line in data.Split('\n'))
        sb.Append("data: ").Append(line).Append('\n');
    sb.Append('\n');
    await res.WriteAsync(sb.ToString());
    await res.Body.FlushAsync();
}

record CreateRequest(string Name);
record SelectRequest(int[] Indices);
record HintRequest(int[] Selection);
record PingRequest(int PingMs);
