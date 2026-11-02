using Newtonsoft.Json;

namespace DepupCode.Models;

public sealed class EnvironmentVariable
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Target { get; set; } = "Production";
    public string Category { get; set; } = "Production Secrets";
    public bool IsSecret { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsRevealed { get; set; }

    [JsonIgnore]
    public bool IsSelected { get; set; }

    [JsonIgnore]
    public string DisplayValue => IsSecret && !IsRevealed ? Mask(Value) : Value;

    [JsonIgnore]
    public string TypeLabel => IsSecret ? "DPAPI AES-256" : "Public Config";

    [JsonIgnore]
    public string TypeColor => IsSecret ? "#FBBF24" : "#34D399";

    private static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= 8) return new string('•', Math.Max(6, value.Length));
        var head = value[..Math.Min(14, value.Length / 3)];
        return head + new string('•', 12) + value[^Math.Min(4, value.Length / 4)..];
    }
}
