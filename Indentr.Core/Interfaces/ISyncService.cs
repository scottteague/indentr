namespace Indentr.Core.Interfaces;

public enum SyncStatus { Success, Offline, Failed }

public sealed record SyncResult(SyncStatus Status, string? Message = null)
{
    public static SyncResult Success           => new(SyncStatus.Success);
    public static SyncResult Offline           => new(SyncStatus.Offline);
    public static SyncResult Fail(string message) => new(SyncStatus.Failed, message);
}

public interface ISyncService
{
    /// <summary>Runs one full push+pull cycle. Returns immediately if no remote is configured.</summary>
    Task<SyncResult> SyncOnceAsync();

    /// <summary>Returns the timestamp of the last successful sync, or DateTimeOffset.MinValue if never synced.</summary>
    Task<DateTimeOffset> GetLastSyncedAtAsync();
}
