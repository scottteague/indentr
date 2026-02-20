using Organiz.Core.Models;

namespace Organiz.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByUsernameAsync(string username);
    Task<User> GetOrCreateAsync(string username);
}
