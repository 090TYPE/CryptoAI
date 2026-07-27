using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CryptoAITerminal.AIEngine;
using Xunit;

namespace CryptoAITerminal.Core.Tests;

/// <summary>
/// When a CryptoAI server is configured, ChatClient routes AI calls to the server's proxy
/// with the license token (server holds the vendor key); otherwise it goes direct-to-vendor.
/// </summary>
[Collection("AiVendorSerial")]
public class ChatClientServerRoutingTests
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
    public async Task Routes_through_server_with_license_header_and_no_vendor_key()
    {
        var handler = new CapturingHandler("{\"content\":[{\"type\":\"text\",\"text\":\"hello from server\"}]}");
        var prevVendor = AiRuntime.Vendor;
        try
        {
            AiRuntime.Vendor = AiVendor.Anthropic;
            ChatClient.ServerBaseUrl = "https://api.example.com";
            ChatClient.LicenseTokenProvider = () => "tok-abc";

            var text = await ChatClient.CompleteTextAsync(
                apiKey: "", model: "claude-sonnet-4-6", maxTokens: 50, temperature: null,
                system: "sys", userContent: "hi", feature: AiFeatureIds.TokenSecurity,
                http: new HttpClient(handler));

            Assert.Equal("hello from server", text);
            Assert.Equal("https://api.example.com/api/ai/message", handler.Request!.RequestUri!.ToString());
            Assert.Equal("tok-abc", Assert.Single(handler.Request.Headers.GetValues("X-License")));
            Assert.False(handler.Request.Headers.Contains("x-api-key"));
            // The server needs this to know which model to substitute; without it a bound terminal
            // silently keeps whatever model it was built with.
            Assert.Equal("token_security", Assert.Single(handler.Request.Headers.GetValues("X-AI-Feature")));
        }
        finally
        {
            ChatClient.ServerBaseUrl = null;
            ChatClient.LicenseTokenProvider = null;
            AiRuntime.Vendor = prevVendor;
        }
    }

    [Fact]
    public async Task Goes_direct_to_vendor_when_no_server_configured()
    {
        var handler = new CapturingHandler("{\"content\":[{\"type\":\"text\",\"text\":\"x\"}]}");
        var prevVendor = AiRuntime.Vendor;
        try
        {
            AiRuntime.Vendor = AiVendor.Anthropic;
            ChatClient.ServerBaseUrl = null;

            await ChatClient.CompleteTextAsync("sk-key", "claude", 10, null, "s", "u",
                feature: AiFeatureIds.TokenSecurity, http: new HttpClient(handler));

            Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
            Assert.True(handler.Request.Headers.Contains("x-api-key"));
            Assert.False(handler.Request.Headers.Contains("X-License"));
            // On the customer's own key there is no server to interpret the feature, and sending an
            // unknown header straight to the vendor is at best noise.
            Assert.False(handler.Request.Headers.Contains("X-AI-Feature"));
        }
        finally { AiRuntime.Vendor = prevVendor; }
    }
}
