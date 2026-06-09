namespace SetGameServer.GarnetIO;

using SetGameServer.Engine;

/// <summary>
/// Abstraction for the durable backup queue so <see cref="GameService"/>
/// works whether or not Azure Cosmos DB is configured.
/// </summary>
public interface IBackupQueue
{
    void EnqueueGame(GameState state);
    void EnqueueLeaderboard(Dictionary<string, LeaderboardEntry> stats);
}

/// <summary>No-op implementation used when Cosmos is not configured.</summary>
public sealed class NullBackupQueue : IBackupQueue
{
    public void EnqueueGame(GameState state) { }
    public void EnqueueLeaderboard(Dictionary<string, LeaderboardEntry> stats) { }
}
