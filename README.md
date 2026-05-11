# Set Game

An implementation of the popular Set game to create some fun team building
across MSR — now multiplayer.

## Architecture

```
Browser (SPA)                     setgame-client (port 8080)
   ├─ Lobby UI ──────► HTTP ────► ASP.NET Core 10 (.NET) server
   ├─ Game UI                      ├─ /api/games (CRUD + lobby)
   └─ EventSource ◄── SSE ─────────│  /api/games/{id}/{join,leave,select,hint,deal3,restart}
                                   ├─ /api/games/{id}/events  (Server-Sent Events stream)
                                   └─ static SPA (HTML/CSS/JS)
                                        │
                                        │ Garnet C# client (Microsoft.Garnet NuGet)
                                        ▼
                              setgame-server (port 6379)
                                Garnet (Redis-compatible)
                                ├─ KV: game:{id}:state (JSON)
                                ├─ SET: games:index
                                └─ pub/sub: game:{id}:events
```

- **Server** (`server/`) is an ASP.NET Core 10 minimal-API app that holds the
  authoritative game state. It uses the `GarnetClient` API from the
  `Microsoft.Garnet` NuGet package for KV + PUBLISH and a small raw-RESP
  subscriber for SUBSCRIBE.
- **Garnet sidecar** (`ghcr.io/microsoft/garnet`) stores per-game state and
  acts as the pub/sub broker. Each game has its own channel, so updates
  broadcast only to clients connected to that game.
- **Client** (`server/wwwroot/`) is a static SPA served by ASP.NET Core's
  default static-files pipeline. It opens a Server-Sent Events stream to
  receive state pushes from the server.
- **Player identity** is read from request headers
  (`X-MS-CLIENT-PRINCIPAL-ID` / `X-MS-CLIENT-PRINCIPAL-NAME`) injected by the
  hosting environment (MSRHub auth proxy). For local dev a stable
  cookie-pinned anonymous id is used so each browser tab has a consistent
  player identity. Per-player score is tracked: the first player to submit a
  valid set claims it.

## Run locally

```powershell
docker compose up --build
# then open http://localhost:8080
```

## Develop the server outside docker

```powershell
docker compose up -d garnet
cd server
dotnet run --urls http://localhost:8080
# wwwroot/ is served automatically — no env vars required.
```

See `specs/` for the game rules and UX details.
