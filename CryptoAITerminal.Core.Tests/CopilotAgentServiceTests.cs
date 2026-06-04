using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.TerminalUI.Services;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

[Collection("AiVendorSerial")]
public class CopilotAgentServiceTests
{
    private static CopilotAgentService.AccountSnapshot Snap(
        string mode = "paper", decimal cash = 1000m, decimal realized = 0m,
        params CopilotAgentService.PositionLine[] positions)
        => new(mode, cash, realized, positions);

    private static CopilotAgentService.CopilotDataSource Source(CopilotAgentService.AccountSnapshot snap)
        => new(Account: _ => Task.FromResult(snap));

    // ── Scripted Anthropic responses (mirrors AiTraderAgentServiceTests). ──
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public int Calls { get; private set; }
        public ScriptedHandler(IEnumerable<string> responses) => _responses = new Queue<string>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = _responses.Count > 0 ? _responses.Dequeue() : EndTurn("done");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static string ToolUse(string id, string name, string inputJson) =>
        $$"""
        {"stop_reason":"tool_use","content":[
          {"type":"text","text":"Let me check."},
          {"type":"tool_use","id":"{{id}}","name":"{{name}}","input":{{inputJson}}}
        ]}
        """;

    private static string EndTurn(string text) =>
        $$"""
        {"stop_reason":"end_turn","content":[{"type":"text","text":"{{text}}"}]}
        """;

    // ── Offline assistant ──────────────────────────────────────────────────────

    [Fact]
    public async Task Offline_Balance_ReportsCashAndRealized()
    {
        var svc = new CopilotAgentService(Source(Snap(cash: 1234.50m, realized: -50m))) { ApiKey = "" };
        Assert.False(svc.UsesLiveModel);

        var answer = await svc.AskAsync("what's my balance?");

        Assert.True(answer.IsOffline);
        Assert.Equal(0, answer.ToolCalls);
        Assert.Contains("$1,234.50", answer.Text);
        Assert.Contains("paper", answer.Text);
    }

    [Fact]
    public async Task Offline_Risk_FlagsConcentration()
    {
        // Two positions, one is 80% of exposure → concentration warning.
        var big = new CopilotAgentService.PositionLine("BTCUSDT", 0.1m, 40000m, 40000m, 0m); // 4000 notional
        var small = new CopilotAgentService.PositionLine("ETHUSDT", 0.5m, 2000m, 2000m, 0m); // 1000 notional
        var svc = new CopilotAgentService(Source(Snap(cash: 5000m, positions: new[] { big, small }))) { ApiKey = "" };

        var answer = await svc.AskAsync("how concentrated is my risk?");

        Assert.True(answer.IsOffline);
        Assert.Contains("BTCUSDT", answer.Text);
        Assert.Contains("⚠", answer.Text); // over-concentration flag
    }

    [Fact]
    public async Task Offline_Positions_ListsEachHolding()
    {
        var p = new CopilotAgentService.PositionLine("SOLUSDT", -2m, 150m, 140m, 20m); // short, in profit
        var svc = new CopilotAgentService(Source(Snap(positions: new[] { p }))) { ApiKey = "" };

        var answer = await svc.AskAsync("show my positions");

        Assert.Contains("SOLUSDT", answer.Text);
        Assert.Contains("short", answer.Text);
    }

    [Fact]
    public async Task Offline_NoKeyNoData_StillAnswersHelp()
    {
        var svc = new CopilotAgentService() { ApiKey = "" };
        var answer = await svc.AskAsync("help");
        Assert.True(answer.IsOffline);
        Assert.Contains("copilot", answer.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_EmptyQuestion_PromptsForInput()
    {
        var svc = new CopilotAgentService(Source(Snap())) { ApiKey = "" };
        var answer = await svc.AskAsync("   ");
        Assert.True(answer.IsOffline);
        Assert.Equal(0, answer.ToolCalls);
    }

    // ── Agentic (live model) path ───────────────────────────────────────────────

    [Fact]
    public async Task Live_CallsTool_ThenReturnsModelAnswer()
    {
        var pos = new CopilotAgentService.PositionLine("BTCUSDT", 0.1m, 40000m, 41000m, 100m);
        var handler = new ScriptedHandler(new[]
        {
            ToolUse("t1", "get_account", "{}"),
            EndTurn("You hold 0.1 BTC, up $100.")
        });
        var svc = new CopilotAgentService(Source(Snap(positions: new[] { pos })),
            httpForTests: new HttpClient(handler)) { ApiKey = "test-key" };

        Assert.True(svc.UsesLiveModel);

        var answer = await svc.AskAsync("how's my BTC doing?");

        Assert.False(answer.IsOffline);
        Assert.Equal(1, answer.ToolCalls);
        Assert.Contains("BTC", answer.Text);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Live_ApiError_DegradesToOffline()
    {
        // Handler always 500s → runner reports "error" → service falls back to offline.
        var failing = new HttpClient(new AlwaysFailHandler());
        var svc = new CopilotAgentService(Source(Snap(cash: 777m)), httpForTests: failing) { ApiKey = "test-key" };

        var answer = await svc.AskAsync("balance");

        Assert.True(answer.IsOffline);
        Assert.Contains("$777.00", answer.Text);
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("{\"error\":\"boom\"}") });
    }
}
