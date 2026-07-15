using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.AIEngine.Agent;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>The agentic runners (Copilot / autonomous trader) also route through the server
/// proxy when one is configured — tools and messages forward verbatim.</summary>
[Collection("AiVendorSerial")]
public class AgentRunnerServerRoutingTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        private readonly string _response;
        public CapturingHandler(string response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_response) });
        }
    }

    [Fact]
    public async Task ClaudeAgentRunner_routes_through_server()
    {
        var handler = new CapturingHandler("{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}");
        try
        {
            ChatClient.ServerBaseUrl = "https://api.example.com";
            ChatClient.LicenseTokenProvider = () => "tok-xyz";

            var runner = new ClaudeAgentRunner(apiKey: "", http: new HttpClient(handler));
            await runner.RunAsync("sys", "do it", new List<AgentTool>());

            Assert.Equal("https://api.example.com/api/ai/message", handler.Request!.RequestUri!.ToString());
            Assert.Equal("tok-xyz", Assert.Single(handler.Request.Headers.GetValues("X-License")));
            Assert.False(handler.Request.Headers.Contains("x-api-key"));
        }
        finally { ChatClient.ServerBaseUrl = null; ChatClient.LicenseTokenProvider = null; }
    }

    [Fact]
    public async Task OpenAiAgentRunner_routes_through_server()
    {
        var handler = new CapturingHandler("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"done\"}}]}");
        try
        {
            ChatClient.ServerBaseUrl = "https://api.example.com";
            ChatClient.LicenseTokenProvider = () => "tok-xyz";

            var runner = new OpenAiAgentRunner(apiKey: "", http: new HttpClient(handler));
            await runner.RunAsync("sys", "do it", new List<AgentTool>());

            Assert.Equal("https://api.example.com/api/ai/openai", handler.Request!.RequestUri!.ToString());
            Assert.Equal("tok-xyz", Assert.Single(handler.Request.Headers.GetValues("X-License")));
            Assert.False(handler.Request.Headers.Contains("Authorization"));
        }
        finally { ChatClient.ServerBaseUrl = null; ChatClient.LicenseTokenProvider = null; }
    }
}
