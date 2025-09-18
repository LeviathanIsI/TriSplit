using Newtonsoft.Json;
using TriSplit.Core.Interfaces;
using TriSplit.Core.Models;

namespace TriSplit.Core.Services;

public class ProfileStore : IProfileStore
{
    private readonly string _profilesDirectory;

    public ProfileStore(string? profilesDirectory = null)
    {
        _profilesDirectory = profilesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TriSplit",
            "Profiles"
        );
        Directory.CreateDirectory(_profilesDirectory);
    }

    public async Task<IEnumerable<Profile>> GetAllProfilesAsync()
    {
        var profiles = new List<Profile>();
        var files = Directory.GetFiles(_profilesDirectory, "*.json");

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var profile = JsonConvert.DeserializeObject<Profile>(json);
                if (profile != null)
                {
                    profile.FilePath = file;
                    profiles.Add(profile);
                }
            }
            catch
            {
                // Skip invalid files
            }
        }

        return profiles.OrderBy(p => p.Name);
    }

    public async Task<Profile?> GetProfileAsync(Guid id)
    {
        var profiles = await GetAllProfilesAsync();
        return profiles.FirstOrDefault(p => p.Id == id);
    }

    public async Task<Profile> SaveProfileAsync(Profile profile)
    {
        profile.UpdatedAt = DateTime.UtcNow;

        var fileName = $"{profile.Id}.json";
        var filePath = Path.Combine(_profilesDirectory, fileName);

        var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
        await File.WriteAllTextAsync(filePath, json);

        profile.FilePath = filePath;
        return profile;
    }

    public async Task DeleteProfileAsync(Guid id)
    {
        var profile = await GetProfileAsync(id);
        if (profile != null && File.Exists(profile.FilePath))
        {
            File.Delete(profile.FilePath);
        }
    }

    public async Task<Profile?> GetProfileByNameAsync(string name)
    {
        var profiles = await GetAllProfilesAsync();
        return profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}