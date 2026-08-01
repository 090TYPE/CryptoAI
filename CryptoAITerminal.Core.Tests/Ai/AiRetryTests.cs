using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using Xunit;

namespace CryptoAITerminal.Core.Tests.Ai;

/// <summary>
/// Заминка сети не должна подменять ответ модели встроенным расчётом.
///
/// Модель здесь основной источник, эвристика — запас, но раньше разница между ними стиралась на
/// первом же таймауте: каждая панель ловила исключение и молча показывала арифметику. Секунда
/// неответа сервера — и человек весь сеанс читает не то, за что платит, причём внешне неотличимое.
/// <c>AiFailure.IsTransient</c> был написан ровно под это и не использовался нигде.
///
/// Проверяется поведение на границе: что повторяется, что нет, и что повтор ровно один.
/// </summary>
[Collection("AiVendorSerial")]
public class AiRetryTests
{
    /// <summary>Отдаёт заготовленные ответы по очереди и считает попытки.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps;
        public int Calls { get; private set; }

        public ScriptedHandler(params Func<HttpResponseMessage>[] steps) => _steps = new(steps);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (_steps.Count == 0) throw new InvalidOperationException("вызовов больше, чем задано сценарием");
            return Task.FromResult(_steps.Dequeue()());
        }
    }

    private static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent($"{{\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}") };

    private static HttpResponseMessage Fail(HttpStatusCode code) =>
        new(code) { Content = new StringContent("{\"error\":{\"type\":\"overloaded_error\"}}") };

    private static async Task<string> CallAsync(ScriptedHandler handler)
    {
        var prev = AiRuntime.Vendor;
        try
        {
            AiRuntime.Vendor = AiVendor.Anthropic;
            ChatClient.ServerBaseUrl = null;
            return await ChatClient.CompleteTextAsync("sk-key", "claude-sonnet-4-6", 50, null,
                "sys", "hi", feature: AiFeatureIds.MarketInsight, http: new HttpClient(handler));
        }
        finally { AiRuntime.Vendor = prev; }
    }

    [Fact]
    public async Task A_server_stumble_is_retried_rather_than_handed_to_the_heuristic()
    {
        var handler = new ScriptedHandler(() => Fail(HttpStatusCode.ServiceUnavailable), () => Ok("живой ответ"));

        var text = await CallAsync(handler);

        Assert.Equal("живой ответ", text);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Rate_limiting_is_retried_too()
    {
        // 429 приходит, когда сервер занят соседним запросом, а не когда что-то настроено не так.
        var handler = new ScriptedHandler(() => Fail(HttpStatusCode.TooManyRequests), () => Ok("ответ"));

        Assert.Equal("ответ", await CallAsync(handler));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_refusal_that_will_repeat_is_not_retried()
    {
        // Лицензия, модель, размер запроса — второй заход вернёт ровно то же, а платит за него
        // ожидание человека перед пустой панелью.
        var handler = new ScriptedHandler(() => Fail(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<AiCallException>(() => CallAsync(handler));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task An_expired_licence_is_not_retried()
    {
        var handler = new ScriptedHandler(() => Fail(HttpStatusCode.PaymentRequired));

        await Assert.ThrowsAsync<AiCallException>(() => CallAsync(handler));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task The_retry_happens_once_and_then_gives_up()
    {
        // Без верхней границы возвратный отказ превратил бы каждую панель в бесконечный цикл к
        // серверу, который и так не отвечает.
        var handler = new ScriptedHandler(
            () => Fail(HttpStatusCode.ServiceUnavailable),
            () => Fail(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<AiCallException>(() => CallAsync(handler));
        Assert.Equal(ChatClient.Attempts, handler.Calls);
        Assert.Equal(2, ChatClient.Attempts);
    }

    [Fact]
    public async Task A_failure_that_survived_the_retry_is_reported_to_the_status_line()
    {
        ChatClient.ResetHealth();
        var handler = new ScriptedHandler(
            () => Fail(HttpStatusCode.ServiceUnavailable),
            () => Fail(HttpStatusCode.ServiceUnavailable));

        await Assert.ThrowsAsync<AiCallException>(() => CallAsync(handler));

        var failure = ChatClient.LastFailure;
        Assert.NotNull(failure);
        Assert.Equal(AiFeatureIds.MarketInsight, failure!.Value.Feature);
    }

    [Fact]
    public async Task A_call_that_succeeded_on_the_retry_leaves_no_alarm_behind()
    {
        // Иначе строка состояния показывала бы сбой после того, как ответ уже получен.
        ChatClient.ResetHealth();
        var handler = new ScriptedHandler(() => Fail(HttpStatusCode.BadGateway), () => Ok("ок"));

        await CallAsync(handler);

        Assert.Null(ChatClient.LastFailure);
        Assert.NotNull(ChatClient.LastSuccessUtc);
    }
}
