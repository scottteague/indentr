using Indentr.Core.Models;

namespace Indentr.Core.Interfaces;

public interface IScratchpadRepository
{
    Task<Scratchpad> GetOrCreateForUserAsync(Guid userId);
    Task<SaveResult> SaveAsync(Scratchpad scratchpad, string originalHash);
}
