using DepupCode.Models;
using DepupCode.Services;
using Xunit;

namespace DepupCode.Tests;

public sealed class AiProviderCatalogTests
{
    [Fact]
    public void Endpoints_are_normalized()
    {
        Assert.Equal("https://api.groq.com/openai/v1/models",
            AiProviderCatalog.BuildModelsEndpoint("https://api.groq.com/openai/v1/chat/completions/"));
        Assert.Equal("http://localhost:11434/v1/chat/completions",
            AiProviderCatalog.BuildChatEndpoint("http://localhost:11434/v1/"));
    }

    [Fact]
    public void Model_list_is_parsed_sorted_and_deduplicated()
    {
        var ids = AiProviderCatalog.ParseModelIds(
            "{\"data\":[{\"id\":\"models/gemini-2.5-flash\"},{\"id\":\"b\"},{\"id\":\"A\"},{\"id\":\"b\"}]}");
        Assert.Equal(new[] { "A", "b", "gemini-2.5-flash" }, ids);
        Assert.Empty(AiProviderCatalog.ParseModelIds("{\"x\":1}"));
    }

    [Theory]
    [InlineData(400, "No endpoints found that support tool use", true)]
    [InlineData(400, "model does not support tools", true)]
    [InlineData(401, "invalid tool key", false)]
    [InlineData(500, "tool", false)]
    [InlineData(400, "invalid model", false)]
    public void Tool_support_errors_are_detected(int status, string detail, bool expected) =>
        Assert.Equal(expected, AiProviderCatalog.LooksLikeToolsUnsupported(status, detail));

    [Fact]
    public void Legacy_settings_resolve_to_matching_preset()
    {
        Assert.Equal("groq", AiProviderCatalog.ResolveCurrentId(new AppSettings { AiApiBaseUrl = "https://api.groq.com/openai/v1/" }));
        Assert.Equal("ollama", AiProviderCatalog.ResolveCurrentId(new AppSettings { AiApiBaseUrl = "http://localhost:11434/v1" }));
        Assert.Equal("manual", AiProviderCatalog.ResolveCurrentId(new AppSettings { AiApiBaseUrl = "https://x.example/v1" }));
        Assert.Equal("custom-x", AiProviderCatalog.ResolveCurrentId(new AppSettings { AiPresetId = "Custom-X" }));
    }

    [Fact]
    public void Custom_provider_ids_are_unique_slugs()
    {
        Assert.Equal("custom-my-provider", AiProviderCatalog.NewCustomId("My  Provider!!", []));
        Assert.Equal("custom-groq-2", AiProviderCatalog.NewCustomId("groq", ["custom-groq"]));
    }

    [Fact]
    public void Local_presets_do_not_require_a_key_and_ids_are_unique()
    {
        Assert.All(AiProviderCatalog.Presets.Where(p => AiProviderCatalog.IsLocalEndpoint(p.BaseUrl)), p => Assert.False(p.RequiresKey));
        Assert.Equal(AiProviderCatalog.Presets.Count, AiProviderCatalog.Presets.Select(p => p.Id).Distinct().Count());
    }
}
