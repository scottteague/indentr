using Indentr.Core.Models;

namespace Indentr.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User> GetOrCreateAsync(string username);
    Task<User> GetOrCreateWithIdAsync(Guid id, string username);
}
