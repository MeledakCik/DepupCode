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
        Assert.Null(request["tools"]);
        Assert.Null(request["reasoning_effort"]);
    }

    [Fact]
    public void Chat_completion_request_includes_tools_only_when_present()
    {
        var messages = new JArray(new JObject { ["role"] = "user", ["content"] = "hi" });
        var withTools = AiAgentService.CreateChatCompletionRequest("m", messages,
            new JArray(new JObject { ["type"] = "function" }));
        Assert.NotNull(withTools["tools"]);
        Assert.Null(AiAgentService.CreateChatCompletionRequest("m", messages, new JArray())["tools"]);
    }
}
