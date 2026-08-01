using CryptoAITerminal.Server.Common;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Serving;

/// <summary>
/// Switching the upstream from a vendor to an OpenAI-compatible router.
///
/// The trap this guards is quiet: a router names models with a vendor prefix, so changing only the
/// URL leaves the allow-list rejecting every call with "model is not allowed" — which reads like a
/// client bug, not a configuration one.
/// </summary>
public class AiUpstreamTests
{
    private static AiPolicyOptions Options() => AiPolicyOptions.FromConfig(_ => null);

    [Fact]
    public void Router_model_names_are_rejected_until_the_allow_list_moves_too()
    {
        var body = """{"model":"anthropic/claude-sonnet-4.6","max_tokens":100,"messages":[]}""";

        var before = AiRequestPolicy.Apply(body, Options(), AiVendor.Anthropic);
        Assert.False(before.Ok);
        Assert.Contains("not allowed", before.Error);

        var after = AiRequestPolicy.Apply(body,
            Options().WithAllowedModels("anthropic/claude-sonnet-4.6,anthropic/claude-haiku-4.5", null),
            AiVendor.Anthropic);
        Assert.True(after.Ok);
    }

    [Fact]
    public void Replacing_one_vendor_list_leaves_the_other_alone()
    {
        // Switching the Anthropic side to a router must not silently drop the OpenAI models that
        // shipped clients still send.
        var swapped = Options().WithAllowedModels("anthropic/claude-sonnet-4.6", null);
        Assert.Contains("gpt-4o", swapped.AllowedOpenAiModels);
        Assert.DoesNotContain("claude-sonnet-4-6", swapped.AllowedAnthropicModels);
    }

    [Fact]
    public void An_empty_override_changes_nothing()
    {
        // Absent settings must leave the environment-configured list standing, or a box with no
        // settings row would refuse every model it accepted yesterday.
        var same = Options().WithAllowedModels(null, "");
        Assert.Contains("claude-sonnet-4-6", same.AllowedAnthropicModels);
        Assert.Contains("gpt-4o", same.AllowedOpenAiModels);
    }

    [Fact]
    public void The_list_is_still_case_insensitive_after_a_swap()
    {
        var swapped = Options().WithAllowedModels("Anthropic/Claude-Sonnet-4.6", null);
        Assert.Contains("anthropic/claude-sonnet-4.6", swapped.AllowedAnthropicModels);
    }

    [Fact]
    public void Whitespace_around_names_is_tolerated()
    {
        // The value is typed into an admin field, and " a , b " is what a human produces.
        var swapped = Options().WithAllowedModels(" anthropic/claude-sonnet-4.6 , anthropic/claude-haiku-4.5 ", null);
        Assert.Equal(2, swapped.AllowedAnthropicModels.Count);
        Assert.Contains("anthropic/claude-haiku-4.5", swapped.AllowedAnthropicModels);
    }

    [Fact]
    public void A_model_the_server_chose_itself_is_not_checked_against_the_list()
    {
        // Список существует, чтобы клиент не назначил дорогую модель за счёт серверного ключа.
        // Когда имя пришло из живого списка провайдера, сверять его не с чем: автоподбор работал бы
        // только на моделях, известных на момент сборки, то есть ровно до следующего релиза вендора.
        var body = """{"model":"claude-sonnet-4-6","max_tokens":100,"messages":[]}""";

        var chosen = AiRequestPolicy.Apply(body, Options(), AiVendor.Anthropic,
            overrideModel: "anthropic/claude-sonnet-9.9", modelIsServerChosen: true);

        Assert.True(chosen.Ok);
        Assert.Equal("anthropic/claude-sonnet-9.9", chosen.Model);
    }

    [Fact]
    public void What_the_client_asked_for_is_still_checked()
    {
        // Ослабление предыдущего теста не должно распространяться на имя, пришедшее снаружи.
        var body = """{"model":"claude-opus-4-1","max_tokens":100,"messages":[]}""";

        var fromClient = AiRequestPolicy.Apply(body, Options(), AiVendor.Anthropic);

        Assert.False(fromClient.Ok);
        Assert.Contains("not allowed", fromClient.Error);
    }

    [Fact]
    public void A_typo_in_the_vendor_url_does_not_turn_it_into_a_router()
    {
        // Сравнение строк целиком делало «роутером» адрес вендора с лишним слэшем — а его ставят
        // руками в поле админки. Дальше по цепочке AiProxy подставлял ключ anthropic и отправлял
        // его bearer-заголовком настоящему OpenAI: опечатка раскрывала ключ одного вендора другому,
        // а ответ 401 читался как «плохой ключ».
        Assert.False(AiProxyDefaults.LooksLikeRouter(AiProxyDefaults.OpenAi + "/", AiProxyDefaults.OpenAi));
        Assert.False(AiProxyDefaults.LooksLikeRouter(AiProxyDefaults.OpenAi.ToUpperInvariant(), AiProxyDefaults.OpenAi));
        Assert.False(AiProxyDefaults.LooksLikeRouter("https://api.openai.com/v1/chat/completions?x=1", AiProxyDefaults.OpenAi));
        Assert.False(AiProxyDefaults.LooksLikeRouter(AiProxyDefaults.Anthropic, AiProxyDefaults.Anthropic));

        // Настоящий роутер по-прежнему опознаётся — ради этого проверка и существует.
        Assert.True(AiProxyDefaults.LooksLikeRouter("https://nordrouter.com/v1/chat/completions", AiProxyDefaults.OpenAi));
        Assert.True(AiProxyDefaults.LooksLikeRouter("https://nordrouter.com/v1/messages", AiProxyDefaults.Anthropic));

        // Неразбираемый адрес не должен считаться вендорским: иначе мусор в поле молча уводил бы
        // на x-api-key там, где нужен bearer.
        Assert.True(AiProxyDefaults.LooksLikeRouter("не адрес", AiProxyDefaults.OpenAi));
    }

    [Fact]
    public void Vendor_defaults_are_the_real_endpoints()
    {
        // Guards against a stray edit to the constants: a wrong default here would send every
        // request with the server's key to somewhere that is not the vendor.
        Assert.Equal("https://api.anthropic.com/v1/messages", AiProxyDefaults.Anthropic);
        Assert.Equal("https://api.openai.com/v1/chat/completions", AiProxyDefaults.OpenAi);
    }
}
