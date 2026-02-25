using Indentr.Core.Models;

namespace Indentr.Core.Interfaces;

public interface IAttachmentStore
{
    Task<IReadOnlyList<AttachmentMeta>> ListForNoteAsync(Guid noteId);

    /// <summary>Returns the metadata and a readable stream for the attachment's content,
    /// or null if no attachment with that ID exists. The caller is responsible for
    /// disposing the stream.</summary>
    Task<(AttachmentMeta Meta, Stream Content)?> OpenReadAsync(Guid attachmentId);

    Task<AttachmentMeta> StoreAsync(Guid noteId, string filename, string mimeType, Stream content);
    Task DeleteAsync(Guid attachmentId);
    Task PermanentlyDeleteAsync(Guid attachmentId);
}
