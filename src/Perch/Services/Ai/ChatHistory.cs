using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch.Services.Ai;

public sealed class ChatMessageRecord
{
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string Thinking { get; set; } = "";
    public bool IsError { get; set; }
    // The agent's steps (searches, pages read), one per line.
    public string Steps { get; set; } = "";
}

public sealed class ChatRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;
    public string ProviderId { get; set; } = "";
    public string Model { get; set; } = "";
    public List<ChatMessageRecord> Messages { get; set; } = [];
}

public sealed class ChatFile
{
    public List<ChatRecord> Chats { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ChatFile))]
internal sealed partial class ChatJson : JsonSerializerContext;

// The saved conversations: one local file next to Perch's settings (chats.json). They are stored as plain text on
// this computer only; deleting a conversation, or all of them, removes them from the file.
public static class ChatHistory
{
    public const int MaxChats = 100;
    private static string FilePath => Path.Combine(Store.DataFolder, "chats.json");

    public static List<ChatRecord> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize(File.ReadAllText(FilePath), ChatJson.Default.ChatFile) ?? new()).Chats.OrderByDescending(c => c.Updated).ToList();
        }
        catch (Exception)
        {
            try { File.Copy(FilePath, FilePath + ".bad", true); } catch { }
        }
        return [];
    }

    public static void Save(IEnumerable<ChatRecord> chats)
    {
        try
        {
            Directory.CreateDirectory(Store.DataFolder);
            var file = new ChatFile { Chats = chats.OrderByDescending(c => c.Updated).Take(MaxChats).ToList() };
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, ChatJson.Default.ChatFile));
            File.Move(temp, FilePath, true);
        }
        catch (Exception) { /* disk full or the file is locked: the next change will try again */ }
    }
}
