using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using CryptoAITerminal.AIEngine.Agent;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>Marks the serial collection so AiRuntime.Vendor mutations never race the agent-service tests.</summary>
[CollectionDefinition("AiVendorSerial")]
public sealed class AiVendorSerialCollection { }

/// <summary>
/// Covers the Claude/ChatGPT provider switch: ChatClient wire routing, the OpenAI
/// agent runner's tool-use loop, AiRuntime resolution, and an end-to-end Copilot turn
/// driven by scripted OpenAI responses. Every test that flips the global
/// <see cref="AiRuntime.Vendor"/> restores it in a finally so the suite stays clean.
/// </summary>
[Collection("AiVendorSerial")]
public class AiProviderSwitchTests
{
    // ── Captures the last request so we can assert which vendor was hit. ──
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public Uri? LastUri { get; private set; }
        public string? LastAuthHeader { get; private set; }
        public bool LastHadApiKeyHeader { get; private set; }

        public CapturingHandler(params string[] responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastAuthHeader = request.Headers.Authorization?.ToString();
            LastHadApiKeyHeader = request.Headers.Contains("x-api-key");
            var body = _responses.Count > 0 ? _responses.Dequeue() : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static T WithVendor<T>(AiVendor vendor, Func<T> body)
    {
        var prev = AiRuntime.Vendor;
        try { AiRuntime.Vendor = vendor; return body(); }
        finally { AiRuntime.Vendor = prev; }
    }

    // ── AiRuntime ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("openai", AiVendor.OpenAi)]
    [InlineData("chatgpt", AiVendor.OpenAi)]
    [InlineData("anthropic", AiVendor.Anthropic)]
    [InlineData("", AiVendor.Anthropic)]
    [InlineData(null, AiVendor.Anthropic)]
    public void ParseVendor_MapsTokens(string? raw, AiVendor expected)
        => Assert.Equal(expected, AiRuntime.ParseVendor(raw));

    [Fact]
    public void ActiveModel_FollowsVendor()
    {
        WithVendor(AiVendor.OpenAi, () => { Assert.Equal(AiRuntime.OpenAiModel, AiRuntime.ActiveModel); return 0; });
        WithVendor(AiVendor.Anthropic, () => { Assert.Equal(AiRuntime.AnthropicModel, AiRuntime.ActiveModel); return 0; });
    }

    // ── ChatClient wire routing ────────────────────────────────────────────────

    [Fact]
    public async Task ChatClient_OpenAi_HitsOpenAiEndpoint_AndExtractsContent()
    {
        var handler = new CapturingHandler("{\"choices\":[{\"message\":{\"content\":\"hello from gpt\"},\"finish_reason\":\"stop\"}]}");
        var http = new HttpClient(handler);

        var text = await WithVendor(AiVendor.OpenAi, () =>
            ChatClient.CompleteTextAsync("sk-test", "gpt-4o", 100, 0.2, "sys", "hi", http));

        Assert.Equal("hello from gpt", text);
        Assert.Contains("api.openai.com", handler.LastUri!.ToString());
        Assert.Equal("Bearer sk-test", handler.LastAuthHeader);
    }

    [Fact]
    public async Task ChatClient_Anthropic_HitsAnthropicEndpoint_AndExtractsText()
    {
        var handler = new CapturingHandler("{\"content\":[{\"type\":\"text\",\"text\":\"hi from claude\"}]}");
        var http = new HttpClient(handler);

        var text = await WithVendor(AiVendor.Anthropic, () =>
            ChatClient.CompleteTextAsync("sk-ant", "claude-sonnet-4-6", 100, 0.2, "sys", "hi", http));

        Assert.Equal("hi from claude", text);
        Assert.Contains("api.anthropic.com", handler.LastUri!.ToString());
        Assert.True(handler.LastHadApiKeyHeader);
    }

    [Fact]
    public async Task ChatClient_NonSuccess_Throws()
    {
        var failing = new HttpClient(new ThrowingHandler());
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            WithVendor(AiVendor.OpenAi, () =>
                ChatClient.CompleteTextAsync("k", "gpt-4o", 50, null, "s", "u", failing)));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"error\":\"rate\"}") });
    }

    // ── OpenAiAgentRunner tool-use loop (constructed directly — no global state) ──

    [Fact]
    public async Task OpenAiAgentRunner_CallsTool_ThenReturnsAnswer()
    {
        var handler = new ScriptedOpenAi(
            // 1) model asks for the tool
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null," +
            "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_x\",\"arguments\":\"{}\"}}]}}]}",
            // 2) model answers
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"done: 42\"}}]}");

        var runner = new OpenAiAgentRunner("sk-test", "gpt-4o", maxIterations: 4, http: new HttpClient(handler));
        var tools = new List<AgentTool>
        {
            new("get_x", "Gets x.", new { type = "object", properties = new { } },
                (_, _) => Task.FromResult("{\"x\":42}"))
        };

        var result = await runner.RunAsync("sys", "what is x?", tools);

        Assert.Equal("stop", result.StoppedReason);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Contains("42", result.FinalText);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class ScriptedOpenAi : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public int Calls { get; private set; }
        public ScriptedOpenAi(params string[] responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = _responses.Count > 0 ? _responses.Dequeue()
                : "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    // ── End-to-end: Copilot drives the OpenAI runner via the factory ──

    [Fact]
    public async Task Copilot_WithOpenAiVendor_AnswersViaOpenAi()
    {
        var prev = AiRuntime.Vendor;
        try
        {
            AiRuntime.Vendor = AiVendor.OpenAi;
            var handler = new ScriptedOpenAi(
                "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"Your balance is healthy.\"}}]}");
            var data = new CopilotAgentService.CopilotDataSource(
                Account: _ => Task.FromResult(new CopilotAgentService.AccountSnapshot("paper", 1000m, 0m, Array.Empty<CopilotAgentService.PositionLine>())));
            var svc = new CopilotAgentService(data, httpForTests: new HttpClient(handler)) { ApiKey = "sk-test" };

            var answer = await svc.AskAsync("how am I doing?");

            Assert.False(answer.IsOffline);
            Assert.Contains("healthy", answer.Text);
            Assert.Contains("ChatGPT", answer.Source);
        }
        finally { AiRuntime.Vendor = prev; }
    }
}
