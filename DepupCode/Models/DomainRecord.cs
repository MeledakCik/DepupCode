namespace DepupCode.Models;

public sealed class DomainRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Domain { get; set; } = "";
    public string Project { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public bool SyncedToVercel { get; set; }
    public bool SyncedToCloudflare { get; set; }
    public bool SyncedToRailway { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
