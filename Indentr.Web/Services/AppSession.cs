using Indentr.Core.Interfaces;
using Indentr.Core.Models;
using Indentr.Data;
using Indentr.Data.Repositories;
using Indentr.Web.Config;

namespace Indentr.Web.Services;

public enum SessionState { Uninitialized, Initializing, Ready, Error }

/// <summary>
/// Scoped service — one instance per SignalR circuit. Mirrors App.axaml.cs StartupAsync.
/// </summary>
public sealed class AppSession : IAsyncDisposable
{
    public SessionState           State          { get; private set; } = SessionState.Uninitialized;
    public string?                ErrorMessage   { get; private set; }
    public INoteRepository?       Notes          { get; private set; }
    public IUserRepository?       Users          { get; private set; }
    public IScratchpadRepository? Scratchpads    { get; private set; }
    public IAttachmentStore?      Attachments    { get; private set; }
    public ISyncService?          Sync           { get; private set; }
    public User?                  CurrentUser    { get; private set; }
    public DatabaseProfile?       CurrentProfile { get; private set; }

    private CancellationTokenSource? _timerCts;

    public async Task InitializeAsync(DatabaseProfile profile)
    {
        State = SessionState.Initializing;
        try
        {
            CurrentProfile = profile;

            var schemaName = string.IsNullOrEmpty(profile.LocalSchemaId)
                ? null
                : $"indentr_{profile.LocalSchemaId}";

            var cs = ConnectionStringBuilder.Build(
                profile.Database.Host, profile.Database.Port,
                profile.Database.Name, profile.Database.Username, profile.Database.Password,
                schemaName);

            await new DatabaseMigrator(cs).MigrateAsync(schemaName);

            Notes       = new NoteRepository(cs);
            Users       = new UserRepository(cs);
            Scratchpads = new ScratchpadRepository(cs);
            Attachments = new PostgresAttachmentStore(cs);

            var remoteCs = profile.RemoteDatabase is { } rdb
                ? ConnectionStringBuilder.Build(rdb.Host, rdb.Port, rdb.Name, rdb.Username, rdb.Password)
                : null;

            Sync = new SyncService(cs, remoteCs, CurrentUser?.Id ?? Guid.Empty);

            CurrentUser = await Users.GetOrCreateAsync(profile.Username);

            // Re-create SyncService now that we have the real user ID.
            Sync = new SyncService(cs, remoteCs, CurrentUser.Id);

            await Notes.EnsureRootExistsAsync(CurrentUser.Id);
            await Scratchpads.GetOrCreateForUserAsync(CurrentUser.Id);

            if (remoteCs is not null)
                StartSyncTimer();

            State = SessionState.Ready;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            State = SessionState.Error;
        }
    }

    private void StartSyncTimer()
    {
        _timerCts = new CancellationTokenSource();
        var token = _timerCts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                    await Sync!.SyncOnceAsync();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_timerCts is not null)
        {
            await _timerCts.CancelAsync();
            _timerCts.Dispose();
        }
    }
}
