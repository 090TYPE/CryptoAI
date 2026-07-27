using System.Text.Json;
using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Per-feature model assignment: the server decides which model a client's call runs on.
///
/// The risk here is asymmetric. Getting an override wrong sends paid traffic to the wrong model
/// silently — no error, just a different bill and a different answer quality — so the tests care
/// most about what happens when nothing is configured and when a terminal is older or newer than
/// the server.
/// </summary>
public class AiFeatureOverrideTests
{
    private static AiPolicyOptions Options() => AiPolicyOptions.FromConfig(_ => null);

    private static string Body(string model = "claude-sonnet-4-6", int maxTokens = 700) =>
        JsonSerializer.Serialize(new { model, max_tokens = maxTokens, messages = new[] { new { role = "user", content = "hi" } } });

    private static (string Model, int MaxTokens) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("model").GetString()!,
                doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void No_override_leaves_the_client_request_alone()
    {
        // The compatibility case that matters most: every terminal sold before per-feature control
        // existed sends no feature header, so the server must behave exactly as it did.
        var r = AiRequestPolicy.Apply(Body(), Options(), AiVendor.Anthropic);
        Assert.True(r.Ok);
        Assert.Equal("claude-sonnet-4-6", Parse(r.Body!).Model);
    }

    [Fact]
    public void Override_replaces_the_model_the_client_asked_for()
    {
        var r = AiRequestPolicy.Apply(Body(), Options(), AiVendor.Anthropic, overrideModel: "claude-haiku-4-5-20251001");
        Assert.True(r.Ok);
        Assert.Equal("claude-haiku-4-5-20251001", Parse(r.Body!).Model);
        Assert.Equal("claude-haiku-4-5-20251001", r.Model);   // reported back for the response header
    }

    [Fact]
    public void Override_that_is_not_allowed_is_refused_rather_than_ignored()
    {
        // An operator typo must be visible. Falling back to the client's model would look like the
        // setting had been ignored, and the wrong-but-working behaviour is the hardest to notice.
        var r = AiRequestPolicy.Apply(Body(), Options(), AiVendor.Anthropic, overrideModel: "claude-3-5-haiku-20241022");
        Assert.False(r.Ok);
        Assert.Contains("not allowed", r.Error);
    }

    [Fact]
    public void Feature_cap_shortens_the_answer_but_cannot_lift_the_global_ceiling()
    {
        var opts = Options();

        var shortened = AiRequestPolicy.Apply(Body(maxTokens: 2000), opts, AiVendor.Anthropic, overrideMaxTokens: 300);
        Assert.Equal(300, Parse(shortened.Body!).MaxTokens);

        var greedy = AiRequestPolicy.Apply(Body(maxTokens: 100), opts, AiVendor.Anthropic, overrideMaxTokens: 99_999);
        Assert.Equal(opts.MaxTokensCap, Parse(greedy.Body!).MaxTokens);
    }

    [Theory]
    [InlineData("signal_desk")]
    [InlineData("token_security")]
    [InlineData("agent")]
    public void Catalogued_features_are_known(string id) => Assert.True(AiFeatures.IsKnown(id));

    [Fact]
    public void Unknown_but_well_formed_feature_is_accepted()
    {
        // A terminal newer than the server names a feature the server has never heard of. It must
        // find no override and run the client's own request, not be rejected — otherwise shipping
        // a client feature would require deploying the server first, in lockstep, forever.
        Assert.False(AiFeatures.IsKnown("feature_from_a_newer_build"));
        Assert.True(AiFeatures.IsWellFormed("feature_from_a_newer_build"));
    }

    [Theory]
    [InlineData("ai.enabled; DROP TABLE server_settings")]
    [InlineData("../../secrets")]
    [InlineData("has space")]
    [InlineData("")]
    [InlineData(null)]
    public void Malformed_feature_names_are_rejected_before_they_become_a_settings_key(string? id)
    {
        // The header is attacker-controlled and is concatenated into a settings key. Anything that
        // is not a short identifier must not get that far.
        Assert.False(AiFeatures.IsWellFormed(id));
    }

    [Fact]
    public void Feature_ids_are_unique()
    {
        // Two features sharing an id would silently share a model setting, and the second one
        // would be impossible to configure independently.
        var ids = AiFeatures.All.Select(f => f.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_feature_id_is_well_formed()
    {
        foreach (var f in AiFeatures.All)
            Assert.True(AiFeatures.IsWellFormed(f.Id), f.Id);
    }
}
