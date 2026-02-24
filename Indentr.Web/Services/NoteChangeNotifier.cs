namespace Indentr.Web.Services;

/// <summary>
/// Singleton cross-circuit event bus. Fires when a note is saved so that all
/// open browser tabs showing that note can reload their content.
/// </summary>
public sealed class NoteChangeNotifier
{
    public event Action<Guid>? NoteChanged;
    public void NotifyChanged(Guid noteId) => NoteChanged?.Invoke(noteId);
}
