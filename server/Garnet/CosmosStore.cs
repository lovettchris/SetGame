using Azure.Identity;
using Microsoft.Azure.Cosmos;
using System.Net;

namespace SetGameServer.GarnetIO;

using SetGameServer.Engine;

/// <summary>
/// Thin wrapper around <see cref="CosmosClient"/> that manages the
/// database, containers, and CRUD for durable game/leaderboard backup.
/// Authenticates via User Assigned Managed Identity.
/// </summary>
public sealed class CosmosStore : IAsyncDisposable
{
    private readonly CosmosClient _client;
    private readonly string _databaseName;
    private readonly ILogger<CosmosStore> _log;
    private Container _games = null!;
    private Container _leaderboard = null!;

    private const string GamesContainer = "games";
    private const string LeaderboardContainer = "leaderboard";
    private const string LeaderboardDocId = "global";

    public CosmosStore(string endpoint, string databaseName, string? managedIdentityClientId, ILogger<CosmosStore> log)
    {
        var cosmosKey = Environment.GetEnvironmentVariable("COSMOS_KEY");
        var options = new CosmosClientOptions
        {
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase,
            },
        };

        if (!string.IsNullOrEmpty(cosmosKey))
        {
            // Local Cosmos Emulator or key-based auth.
            if (endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("127.0.0.1"))
            {
                options.HttpClientFactory = () =>
                    new HttpClient(new HttpClientHandler
                    {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                    });
                options.ConnectionMode = ConnectionMode.Gateway;
            }
            _client = new CosmosClient(endpoint, cosmosKey, options);
            log.LogInformation("CosmosStore using key-based auth (emulator/local)");
        }
        else
        {
            var credential = string.IsNullOrEmpty(managedIdentityClientId)
                ? new DefaultAzureCredential()
                : new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = managedIdentityClientId,
                });
            _client = new CosmosClient(endpoint, credential, options);
            log.LogInformation("CosmosStore using managed identity auth");
        }

        _databaseName = databaseName;
        _log = log;
    }

    /// <summary>Create the database and containers if they don't exist.</summary>
    public async Task InitializeAsync()
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync(_databaseName);
        _games = (await db.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(GamesContainer, "/id"))).Container;
        _leaderboard = (await db.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties(LeaderboardContainer, "/id"))).Container;
        _log.LogInformation("CosmosStore initialized (db={db})", _databaseName);
    }

    public async Task UpsertGameAsync(GameState state)
    {
        await _games.UpsertItemAsync(state, new PartitionKey(state.Id));
    }

    public async Task UpsertLeaderboardAsync(Dictionary<string, LeaderboardEntry> stats)
    {
        var doc = new LeaderboardDoc { Id = LeaderboardDocId, Stats = stats };
        await _leaderboard.UpsertItemAsync(doc, new PartitionKey(LeaderboardDocId));
    }

    public async Task DeleteGameAsync(string id)
    {
        try
        {
            await _games.DeleteItemAsync<object>(id, new PartitionKey(id));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone — fine.
        }
    }

    /// <summary>Query for games whose <c>lastActivityAt</c> is older than
    /// <paramref name="cutoffMs"/> and delete them.</summary>
    public async Task<int> SweepInactiveGamesAsync(long cutoffMs)
    {
        int deleted = 0;
        var query = new QueryDefinition(
            "SELECT c.id FROM c WHERE c.lastActivityAt < @cutoff")
            .WithParameter("@cutoff", cutoffMs);

        using var feed = _games.GetItemQueryIterator<IdOnly>(query);
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            foreach (var item in page)
            {
                await DeleteGameAsync(item.Id);
                deleted++;
            }
        }
        return deleted;
    }

    /// <summary>Load all persisted games (for cold-start restore).</summary>
    public async Task<List<GameState>> LoadAllGamesAsync()
    {
        var results = new List<GameState>();
        using var feed = _games.GetItemQueryIterator<GameState>("SELECT * FROM c");
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            results.AddRange(page);
        }
        return results;
    }

    /// <summary>Load the persisted leaderboard (for cold-start restore).</summary>
    public async Task<Dictionary<string, LeaderboardEntry>?> LoadLeaderboardAsync()
    {
        try
        {
            var resp = await _leaderboard.ReadItemAsync<LeaderboardDoc>(
                LeaderboardDocId, new PartitionKey(LeaderboardDocId));
            return resp.Resource.Stats;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class LeaderboardDoc
    {
        public string Id { get; set; } = "";
        public Dictionary<string, LeaderboardEntry> Stats { get; set; } = new();
    }

    private sealed class IdOnly
    {
        public string Id { get; set; } = "";
    }
}
