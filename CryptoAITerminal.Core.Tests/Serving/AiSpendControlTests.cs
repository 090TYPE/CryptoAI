using System.Text.Json;
using CryptoAITerminal.Server.Common;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// The two controls standing between a client and the server's AI key. Without the policy the
/// proxy forwards the body verbatim, so the client picks the model, the output length and the
/// context size; without the budget a licence can stay inside the rate limit and still spend the
/// month in an afternoon. The properties worth pinning are that the policy REWRITES what it safely
/// can (so ordinary requests keep working) and refuses only what it cannot, and that the budget
/// blocks at the cap rather than near it.
/// </summary>
public class AiSpendControlTests
{
    private sealed class Clock
    {
        public DateTime Now = new(2026, 7, 25, 23, 30, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static AiPolicyOptions Options(params (string Key, string Value)[] overrides)
    {
        var map = overrides.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase);
        return AiPolicyOptions.FromConfig(k => map.TryGetValue(k, out var v) ? v : null);
    }

    private static JsonElement Body(AiPolicyResult result)
    {
        Assert.True(result.Ok, result.Error);
        Assert.NotNull(result.Body);
        return JsonDocument.Parse(result.Body!).RootElement.Clone();
    }

    [Fact]
    public void Model_outside_the_allowlist_is_refused()
    {
        var request = """{"model":"claude-opus-4-1-20250805","max_tokens":100}""";

        var result = AiRequestPolicy.Apply(request, Options(), AiVendor.Anthropic);

        Assert.False(result.Ok);
        Assert.Null(result.Body);
        Assert.Contains("not allowed", result.Error);
        // The rejected model is echoed back so the refusal is loggable without re-parsing the body.
        Assert.Equal("claude-opus-4-1-20250805", result.Model);
    }

    [Fact]
    public void Allowlisted_model_passes()
    {
        var request = """{"model":"claude-haiku-4-5-20251001","max_tokens":100}""";

        var result = AiRequestPolicy.Apply(request, Options(), AiVendor.Anthropic);
        var body = Body(result);

        Assert.Equal("claude-haiku-4-5-20251001", result.Model);
        Assert.Null(result.Error);
        Assert.Equal(100, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Allowlists_are_per_vendor()
    {
        // An Anthropic id must not be spendable on the OpenAI key and vice versa, otherwise a
        // client could route around whichever list is the looser one.
        Assert.False(AiRequestPolicy.Apply("""{"model":"gpt-4o-mini"}""", Options(), AiVendor.Anthropic).Ok);
        Assert.False(AiRequestPolicy.Apply("""{"model":"claude-sonnet-5"}""", Options(), AiVendor.OpenAi).Ok);
        Assert.True(AiRequestPolicy.Apply("""{"model":"gpt-4o-mini"}""", Options(), AiVendor.OpenAi).Ok);
    }

    [Fact]
    public void Max_tokens_above_the_cap_is_clamped_not_refused()
    {
        var request = """{"model":"claude-sonnet-5","max_tokens":999999}""";

        // Clamping keeps a merely greedy client working; refusing would break the terminal UI for
        // what is a cost problem, not an abuse problem.
        var body = Body(AiRequestPolicy.Apply(request, Options(("AI_MAX_TOKENS_CAP", "1500")), AiVendor.Anthropic));

        Assert.Equal(1500, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Absent_max_tokens_is_pinned_to_the_cap()
    {
        var request = """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""";

        // OpenAI treats the field as optional, so leaving it out would mean "model default" —
        // an unbounded output length the server pays for.
        var body = Body(AiRequestPolicy.Apply(request, Options(("AI_MAX_TOKENS_CAP", "800")), AiVendor.OpenAi));

        Assert.Equal(800, body.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void OpenAi_max_completion_tokens_is_clamped_in_place()
    {
        var request = """{"model":"gpt-4o","max_completion_tokens":50000}""";

        // The newer field wins when present; clamping "max_tokens" instead would leave the real
        // limit untouched.
        var body = Body(AiRequestPolicy.Apply(request, Options(("AI_MAX_TOKENS_CAP", "600")), AiVendor.OpenAi));

        Assert.Equal(600, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Streaming_is_forced_off()
    {
        var request = """{"model":"claude-sonnet-5","max_tokens":10,"stream":true}""";

        // The proxy buffers the upstream body into a string, so an SSE response would reach the
        // client as an unusable blob. Rewriting beats failing after the tokens are already paid for.
        var body = Body(AiRequestPolicy.Apply(request, Options(), AiVendor.Anthropic));

        Assert.False(body.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void Conversation_longer_than_max_messages_is_refused()
    {
        var messages = string.Join(",", Enumerable.Range(0, 4).Select(i => $"{{\"role\":\"user\",\"content\":\"{i}\"}}"));
        var request = $"{{\"model\":\"claude-sonnet-5\",\"max_tokens\":10,\"messages\":[{messages}]}}";
        var options = Options(("AI_MAX_MESSAGES", "3"));

        // Context size is the one dimension that cannot be clamped without silently changing the
        // answer, so an oversized history is the one that has to be refused.
        var refused = AiRequestPolicy.Apply(request, options, AiVendor.Anthropic);

        Assert.False(refused.Ok);
        Assert.Null(refused.Body);
        Assert.Contains("too many messages", refused.Error);

        var atTheLimit = request.Replace(",{\"role\":\"user\",\"content\":\"3\"}", "");
        Assert.True(AiRequestPolicy.Apply(atTheLimit, options, AiVendor.Anthropic).Ok);
    }

    [Fact]
    public void Invalid_json_is_refused()
    {
        var broken = AiRequestPolicy.Apply("{\"model\":", Options(), AiVendor.Anthropic);
        Assert.False(broken.Ok);
        Assert.Null(broken.Body);
        Assert.Contains("valid JSON", broken.Error);

        // Valid JSON that is not an object cannot carry a model either.
        var array = AiRequestPolicy.Apply("[]", Options(), AiVendor.Anthropic);
        Assert.False(array.Ok);
        Assert.Contains("JSON object", array.Error);

        var noModel = AiRequestPolicy.Apply("""{"max_tokens":10}""", Options(), AiVendor.Anthropic);
        Assert.False(noModel.Ok);
        Assert.Null(noModel.Model);
    }

    [Fact]
    public void FromConfig_takes_overrides_and_ignores_unusable_values()
    {
        var configured = Options(
            ("AI_ALLOWED_ANTHROPIC_MODELS", " claude-sonnet-5 , claude-3-5-haiku-20241022 "),
            ("AI_MAX_TOKENS_CAP", "250"),
            ("AI_MAX_MESSAGES", "0"),
            ("AI_DAILY_TOKENS_PER_LICENSE", "not a number"));

        Assert.Equal(2, configured.AllowedAnthropicModels.Count);
        Assert.Contains("claude-sonnet-5", configured.AllowedAnthropicModels);
        Assert.Equal(250, configured.MaxTokensCap);
        // A zero or unparsable value is an operator mistake, not an invitation to disable the
        // control, so the default stands. Compared against the default-built options rather than a
        // literal: what matters is that the fallback happens, and pinning the number here only
        // meant this test had to be edited every time a default was legitimately retuned.
        var defaults = Options();
        Assert.Equal(defaults.MaxMessages, configured.MaxMessages);
        Assert.Equal(defaults.DailyTokenCapPerLicense, configured.DailyTokenCapPerLicense);
    }

    [Fact]
    public void CountUsage_reads_each_vendors_usage_block()
    {
        var anthropic = """{"id":"msg_1","usage":{"input_tokens":120,"output_tokens":30}}""";
        var openAi = """{"id":"chatcmpl-1","usage":{"prompt_tokens":200,"completion_tokens":50,"total_tokens":250}}""";

        Assert.Equal(150, AiRequestPolicy.CountUsage(anthropic, AiVendor.Anthropic));
        Assert.Equal(250, AiRequestPolicy.CountUsage(openAi, AiVendor.OpenAi));

        // Field names are vendor specific: reading the wrong pair would charge nothing and turn
        // the budget into decoration.
        Assert.Equal(0, AiRequestPolicy.CountUsage(anthropic, AiVendor.OpenAi));
        Assert.Equal(0, AiRequestPolicy.CountUsage(openAi, AiVendor.Anthropic));
    }

    [Fact]
    public void CountUsage_charges_nothing_for_a_body_it_cannot_read()
    {
        Assert.Equal(0, AiRequestPolicy.CountUsage("<html>502 Bad Gateway</html>", AiVendor.Anthropic));
        Assert.Equal(0, AiRequestPolicy.CountUsage("{}", AiVendor.Anthropic));
        Assert.Equal(0, AiRequestPolicy.CountUsage("""{"usage":{}}""", AiVendor.OpenAi));
    }

    [Fact]
    public void Budget_blocks_at_the_cap_and_not_before()
    {
        var budget = new AiBudget(1000);

        budget.Charge("lic-a", 999);
        Assert.True(budget.HasHeadroom("lic-a"));
        Assert.Equal(1, budget.Remaining("lic-a"));

        // Exactly at the cap is spent: a request costing far more than the leftover token would
        // otherwise sail through on a one-token allowance.
        budget.Charge("lic-a", 1);
        Assert.Equal(1000, budget.Used("lic-a"));
        Assert.False(budget.HasHeadroom("lic-a"));
        Assert.Equal(0, budget.Remaining("lic-a"));

        // Overshoot is possible because usage is only known after the call, but it must not go
        // negative on the way out.
        budget.Charge("lic-a", 5000);
        Assert.Equal(0, budget.Remaining("lic-a"));
    }

    [Fact]
    public void One_licence_spending_its_day_does_not_touch_another()
    {
        var budget = new AiBudget(1000);

        budget.Charge("lic-a", 1000);

        Assert.False(budget.HasHeadroom("lic-a"));
        Assert.True(budget.HasHeadroom("lic-b"));
        Assert.Equal(0, budget.Used("lic-b"));
        Assert.Equal(1000, budget.Remaining("lic-b"));
    }

    [Fact]
    public void Budget_resets_on_the_utc_day_boundary()
    {
        var clock = new Clock();
        var budget = new AiBudget(1000, clock.Get);

        budget.Charge("lic-a", 1000);
        Assert.False(budget.HasHeadroom("lic-a"));

        clock.Advance(TimeSpan.FromMinutes(31)); // 2026-07-26 00:01 UTC
        Assert.Equal(0, budget.Used("lic-a"));
        Assert.True(budget.HasHeadroom("lic-a"));

        // The stale total must be dropped, not added to, when the new day is first charged.
        budget.Charge("lic-a", 10);
        Assert.Equal(10, budget.Used("lic-a"));
    }

    [Fact]
    public void Empty_licence_never_has_headroom()
    {
        var budget = new AiBudget(1000);

        // An unauthenticated or malformed caller would otherwise share one anonymous allowance.
        Assert.False(budget.HasHeadroom(""));

        budget.Charge("", 500);
        Assert.Equal(0, budget.Used(""));
        Assert.False(budget.HasHeadroom(""));
    }
}
