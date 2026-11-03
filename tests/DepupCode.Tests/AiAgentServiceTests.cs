using DepupCode.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DepupCode.Tests;

public sealed class AiAgentServiceTests
{
    [Fact]
    public void Gemini_chat_completion_request_uses_openai_compatible_fields_only()
    {
        var request = AiAgentService.CreateChatCompletionRequest(
            "gemini-2.5-flash",
            new JArray(new JObject { ["role"] = "user", ["content"] = "hello" }),
            new JArray());

        Assert.Equal("gemini-2.5-flash", request["model"]?.Value<string>());
        Assert.NotNull(request["messages"]);
        Assert.NotNull(request["tools"]);
        Assert.Null(request["reasoning_effort"]);
    }
}
