using DepupCode.Models;
using Newtonsoft.Json;

namespace DepupCode.Services;

public sealed class AiChatStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepupCode", "ai-chats.json");

    public List<AiChatSession> Load()
    {
        try
        {
            if (File.Exists(_path))
                return (JsonConvert.DeserializeObject<List<AiChatSession>>(SecretStore.Unprotect(File.ReadAllText(_path))) ?? [])
                    .OrderByDescending(item => item.UpdatedUtc).Take(40).ToList();
        }
        catch { }
        return [];
    }

    public void Save(IEnumerable<AiChatSession> sessions)
    {
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        var tempPath = _path + ".tmp";
        var json = JsonConvert.SerializeObject(sessions.OrderByDescending(item => item.UpdatedUtc).Take(40), Formatting.Indented);
        File.WriteAllText(tempPath, SecretStore.Protect(json));
        File.Move(tempPath, _path, true);
    }
}
