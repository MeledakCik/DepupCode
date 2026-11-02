namespace DepupCode.Models;

public sealed class AiChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Chat baru";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public string ContextSummary { get; set; } = "";
    public int ContextSummaryThroughTurn { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public List<AiChatTurn> Turns { get; set; } = [];
    public List<AiChatArtifact> Artifacts { get; set; } = [];
}

public sealed class AiChatArtifact
{
    public string Type { get; set; } = "Info";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string? FilePath { get; set; }
    public string? WorkspaceRelativePath { get; set; }
    public string? RollbackBackupPath { get; set; }
    public bool CreatedByAgent { get; set; }
    public string? ExpectedFileHash { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AiChatTurn
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public Newtonsoft.Json.Linq.JArray? ToolCalls { get; set; }
    public string? ToolCallId { get; set; }
    public string? Name { get; set; }

    // Preview images are sent once to the model and never persisted in the chat archive.
    [Newtonsoft.Json.JsonIgnore]
    public string? EphemeralImageDataUrl { get; set; }
}

public sealed class AiCustomAgent
{
    public string Name { get; set; } = "";
    public string Instructions { get; set; } = "";
}

public sealed record AiToolRequest(string Action, string Target, string Details);

public sealed record AiExternalTool(
    string Name,
    string Description,
    Newtonsoft.Json.Linq.JObject Parameters,
    bool ReadOnly,
    Func<bool> IsAvailable,
    Func<AiChatSession, Newtonsoft.Json.Linq.JObject, Func<AiToolRequest, Task<bool>>, CancellationToken, Task<string>> Execute);
