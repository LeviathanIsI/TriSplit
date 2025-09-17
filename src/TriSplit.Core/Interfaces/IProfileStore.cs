using TriSplit.Core.Models;

namespace TriSplit.Core.Interfaces;

public interface IProfileStore
{
    Task<IEnumerable<Profile>> GetAllProfilesAsync();
    Task<Profile?> GetProfileAsync(Guid id);
    Task<Profile> SaveProfileAsync(Profile profile);
    Task DeleteProfileAsync(Guid id);
    Task<Profile?> GetProfileByNameAsync(string name);
}