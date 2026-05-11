# SetGame Architecture

A multiplayer Set card game with **server‑authoritative state** and
**Garnet** as the only persistence + real‑time fan‑out layer. The browser
is a thin SPA: it POSTs intents (join, select, hint, …) and reflects
state pushed back to it over Server‑Sent Events (SSE).

The whole real‑time loop is just **two Garnet primitives** per game:

| Concern              | Garnet primitive          | Key / channel              |
| -------------------- | ------------------------- | -------------------------- |
| Game state blob      | `STRING` (JSON)           | `game:{id}:state`          |
| Lobby index          | `SET`                     | `games:index`              |
| Real‑time fan‑out    | `PUBLISH` / `SUBSCRIBE`   | `game:{id}:events`         |

Every state mutation is **`SET ... ; PUBLISH ...`** under a per‑process
write lock — so persistence and broadcast are produced from the same
in‑memory object, and every connected client sees the same authoritative
snapshot.

---

## High‑level component diagram

```mermaid
flowchart LR
    subgraph Browser["Browser SPA  (wwwroot)"]
        UI["game.js / lobby.js<br/>UI rendering"]
        Client["client.js<br/>SetClient API helper"]
    end

    subgraph Server["ASP.NET Core minimal API  (server/)"]
        API["/api/games/*<br/>HTTP intents"]
        SSE["/api/games/{id}/events<br/>SSE stream"]
        SVC["GameService<br/>orchestration + race fairness"]
        ENG["SetGameEngine<br/>pure rules"]
        STORE["GarnetGameStore<br/>STRING SET/GET, SADD/SREM"]
        SUB["GarnetSubscriber<br/>raw‑RESP pub/sub bridge"]
    end

    subgraph Garnet["Garnet  (microsoft/garnet, RESP-compatible)"]
        KV[("STRING<br/>game:{id}:state")]
        IDX[("SET<br/>games:index")]
        CH(("PUB/SUB<br/>game:{id}:events"))
    end

    UI --> Client
    Client -- "POST intent + X-SetGame-Player-* headers" --> API
    Client -- "EventSource (SSE)" --> SSE
    API --> SVC
    SVC --> ENG
    SVC --> STORE
    STORE --> KV
    STORE --> IDX
    SVC -- "PUBLISH after each mutation" --> CH
    CH --> SUB
    SUB --> SSE
    SSE -- "event: state\ndata: {full GameState JSON}" --> Client
```

Every browser holds **one EventSource per game**. The server holds **one
Garnet subscription per game**, multiplexed across all connected
browsers via an in‑process fan‑out (`ChannelFanout`).

---

## Real‑time write/broadcast loop

The interesting story is what happens when *anyone* does anything in a
game — say Alice clicks "submit a set".

```mermaid
sequenceDiagram
    autonumber
    actor Alice
    actor Bob
    participant ABrowser as Alice browser
    participant BBrowser as Bob browser
    participant API as ASP.NET API
    participant SVC as GameService
    participant ENG as SetGameEngine
    participant STORE as GarnetGameStore
    participant G as Garnet
    participant SUB as GarnetSubscriber
    participant SSE_A as SSE → Alice
    participant SSE_B as SSE → Bob

    Note over ABrowser,BBrowser: Both browsers already have an open<br/>EventSource on /api/games/{id}/events.<br/>Server holds ONE Garnet SUBSCRIBE per game,<br/>fanned out to N listeners.

    Alice->>ABrowser: clicks 3rd card
    ABrowser->>API: POST /api/games/{id}/select<br/>(+ X-SetGame-Player-Id/Name)
    API->>SVC: SubmitAsync(id, playerId, indices)

    Note over SVC: Race-fairness window:<br/>collect all submissions<br/>arriving within max-ping ms,<br/>sort by ping-adjusted click time.

    SVC->>STORE: LockAsync()  (per-process write gate)
    STORE->>G: GET game:{id}:state
    G-->>STORE: JSON
    SVC->>ENG: SubmitSet(state, playerId, indices)
    ENG-->>SVC: outcome + updated state (Version++)
    SVC->>STORE: SET game:{id}:state = new JSON
    STORE->>G: STRING SET
    SVC->>STORE: PUBLISH game:{id}:events = new JSON
    STORE->>G: PUBLISH

    par Garnet fans the message back to the (one) subscriber
        G-->>SUB: message  game:{id}:events  {payload}
        SUB-->>SSE_A: ChannelFanout.Publish(payload)
        SUB-->>SSE_B: ChannelFanout.Publish(payload)
    end

    SSE_A-->>ABrowser: event: state\ndata: {GameState}
    SSE_B-->>BBrowser: event: state\ndata: {GameState}

    ABrowser->>Alice: render — score sparkle, deal flip
    BBrowser->>Bob:   render — score sparkle, deal flip

    Note over ABrowser,BBrowser: Clients version-gate (state.version > lastVersion)<br/>so retries / out-of-order pushes are dropped.
```

### Why publish *after* setting the same JSON we just stored?

Because the SSE handler subscribes to `game:{id}:events` and treats every
published payload as *the* new authoritative state. There is no separate
"event" model: **the broadcast payload is the full state blob.** That
keeps the protocol trivially recoverable — a freshly opened browser gets
the same `state` event shape from the initial `GET` as it does from
every subsequent `PUBLISH`.

---

## How a browser joins a game

```mermaid
sequenceDiagram
    autonumber
    actor User
    participant SPA as Browser SPA
    participant GK as MSRHub gatekeeper
    participant API as ASP.NET API
    participant SVC as GameService
    participant G as Garnet

    User->>SPA: open /game.html?id=abc
    SPA->>GK: GET /definitions/whoami
    alt gatekeeper succeeds
        GK-->>SPA: "alice@microsoft.com"
        Note over SPA: id = full email,<br/>name = alias before "@"
    else gatekeeper unreachable / not signed in
        SPA->>API: GET /api/games (touches PlayerIdentity)
        API-->>SPA: Set-Cookie setgame_pid=anon-...
        Note over SPA: name = "Guest <suffix>"
    end

    SPA->>API: POST /api/games/abc/join<br/>X-SetGame-Player-Id, X-SetGame-Player-Name
    API->>SVC: JoinAsync(...)
    SVC->>G: SET + PUBLISH (new state)

    SPA->>API: GET /api/games/abc/events  (EventSource)
    API->>G: SUBSCRIBE game:abc:events  (first listener only)
    API-->>SPA: event: state (initial snapshot from GET)

    loop for every subsequent mutation by anyone
        G-->>API: message on game:abc:events
        API-->>SPA: event: state (full GameState JSON)
    end
```

Identity precedence on the server (`PlayerIdentity.From`):

1. `X-SetGame-Player-Id` / `X-SetGame-Player-Name` — supplied by the SPA
   (gatekeeper‑resolved username, URL‑encoded).
2. `X-MS-CLIENT-PRINCIPAL-ID` / `-NAME` — MSRHub auth proxy headers.
3. `setgame_pid` cookie — local‑dev anonymous fallback ("Guest …").

---

## Garnet keys and channels at a glance

```
games:index                          SET     all known game ids (lobby)
game:{id}:state                      STRING  full GameState JSON
game:{id}:events                     PUB/SUB broadcast of every persisted state
```

A single mutation always does, **inside one `LockAsync()`**:

```
GET     game:{id}:state
…mutate in process, bump state.Version, set state.LastActivityAt = now…
SET     game:{id}:state  <new JSON>
PUBLISH game:{id}:events <same new JSON>
```

This is in `GameService.MutateAsync` and is the single chokepoint for
*every* state change (join, leave, submit, hint, deal3, restart,
new‑round, ping). Returning `false` from the mutation callback skips
both the SET and the PUBLISH — used when an action is rejected without
touching state (e.g. losing a race).

---

## Pub/sub bridge: one TCP connection, many subscribers

The high‑level Garnet C# client doesn't expose a SUBSCRIBE callback, so
`GarnetSubscriber` opens a **dedicated TCP connection** in SUBSCRIBE
mode and speaks raw RESP:

```mermaid
flowchart TB
    subgraph Process["Single ASP.NET process"]
        direction LR
        A1[SSE handler<br/>browser A] -->|AddListener| F1[ChannelFanout<br/>game:abc:events]
        A2[SSE handler<br/>browser B] -->|AddListener| F1
        A3[SSE handler<br/>browser C] -->|AddListener| F2[ChannelFanout<br/>game:xyz:events]
        F1 --> RL[ReadLoopAsync]
        F2 --> RL
        RL --> TCP["one TCP socket<br/>(SUBSCRIBE mode)"]
    end
    TCP <-->|RESP| Garnet[(Garnet)]
```

* First listener on a channel issues `SUBSCRIBE channel` to Garnet.
* Last listener leaving issues `UNSUBSCRIBE channel`.
* Every incoming `["message", channel, payload]` is dispatched to the
  matching `ChannelFanout`, which writes to each listener's
  `Channel<string>` reader. The SSE handler then formats the payload as
  `event: state\ndata: …\n\n`.

Writes (SET, PUBLISH, SADD, …) go through the **separate**
`GarnetClient` connection in `GarnetGameStore`. Read‑modify‑write is
serialized in the server process by `GarnetGameStore.LockAsync()` —
sufficient because there is exactly one writer process per Garnet
deployment.

---

## Race fairness for simultaneous "set!" submissions

Two players who both spot the same set within milliseconds will arrive
at the server out of order due to network jitter. `GameService` collects
submissions in a short window (sized by the slowest known ping) and
resolves them in **one** `MutateAsync`, so one combined broadcast covers
every set found in that window:

```mermaid
sequenceDiagram
    participant A as Alice (low ping)
    participant B as Bob (high ping)
    participant SVC as GameService
    participant G as Garnet

    A->>SVC: select [3,5,7]   t=100ms (adj.)
    B->>SVC: select [3,5,7]   t=95ms  (adj.)
    Note over SVC: Both queued in one RaceQueue.<br/>Timer fires after window.
    SVC->>SVC: sort by adjusted click time → Bob first
    SVC->>G: SET + single PUBLISH<br/>(Bob scored, Alice rejected with "Bob beat you")
    G-->>A: state push (sees Bob's score, sees error reason)
    G-->>B: state push (sees own score, sparkle)
```

If the two submissions are **disjoint sets**, both are accepted in the
same mutation; the broadcast carries `ScoredPlayerIds[]` and the
combined `LastDealtIndices` so both deals animate together on every
client.

---

## Garbage collection

* **Inactive players** — periodic timer (`SweepInactivePlayersAsync`,
  every 5 s) flips a player to `Active=false` if no `/ping` arrived
  within 30 s. The flip itself goes through `MutateAsync` so every
  client immediately sees the dim row.
* **Stale games** — `SweepStaleGamesAsync` runs every 10 min: any game
  whose `LastActivityAt` is older than 24 h is `DELETE`d from
  `game:{id}:state`, removed from `games:index`, and dropped from
  in‑process race / ping caches.
* **Lobby cap** — `ListAsync` returns at most 1000 games, sorted by
  `StartedAt` descending.

---

## File map

| Layer            | Files                                                            |
| ---------------- | ---------------------------------------------------------------- |
| Rules engine     | `server/Engine/SetGameEngine.cs`, `server/Engine/GameState.cs`   |
| Orchestration    | `server/Games/GameService.cs`, `server/Games/PlayerIdentity.cs`  |
| Garnet wiring    | `server/Garnet/GarnetGameStore.cs`, `server/Garnet/GarnetSubscriber.cs` |
| HTTP / SSE edges | `server/Program.cs`                                              |
| SPA              | `server/wwwroot/js/{client,lobby,game,cards}.js`, `wwwroot/*.html` |
| Container        | `docker-compose.yml` (runs Garnet on `localhost:6379`)           |
