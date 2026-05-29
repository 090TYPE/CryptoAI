using System;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CryptoAITerminal.TerminalUI.Services;

/// <summary>
/// SMTP email отправитель. Использует System.Net.Mail.SmtpClient — встроен в .NET.
/// Поддерживает Gmail/Outlook/Yandex/own SMTP. STARTTLS включается через EnableSsl=true.
/// Для Gmail нужен App Password (двухфакторная аутентификация → Security → App passwords),
/// обычный пароль учётки SMTP-сервером отвергается.
/// </summary>
public sealed class EmailNotificationService : IDisposable
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private string _host       = string.Empty;
    private int    _port       = 587;
    private bool   _useSsl     = true;
    private string _username   = string.Empty;
    private string _password   = string.Empty;
    private string _fromAddress = string.Empty;
    private string _toAddress   = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_host) &&
        _port > 0 &&
        !string.IsNullOrWhiteSpace(_username) &&
        !string.IsNullOrWhiteSpace(_password) &&
        IsValidEmail(_fromAddress) &&
        IsValidEmail(_toAddress);

    public void Configure(
        string host, int port, bool useSsl,
        string username, string password,
        string fromAddress, string toAddress)
    {
        _host        = (host ?? string.Empty).Trim();
        _port        = port > 0 ? port : 587;
        _useSsl      = useSsl;
        _username    = (username ?? string.Empty).Trim();
        _password    = password ?? string.Empty;
        _fromAddress = (fromAddress ?? string.Empty).Trim();
        _toAddress   = (toAddress ?? string.Empty).Trim();
    }

    public async Task<bool> SendAsync(string subject, string body)
    {
        if (!IsConfigured) return false;

        try
        {
            using var client = new SmtpClient(_host, _port)
            {
                EnableSsl = _useSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(_username, _password),
                Timeout = 15_000
            };

            using var message = new MailMessage
            {
                From = new MailAddress(_fromAddress, "CryptoAI Terminal"),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(new MailAddress(_toAddress));

            await client.SendMailAsync(message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> TestConnectionAsync() =>
        IsConfigured
            ? SendAsync(
                "CryptoAI Terminal — Email channel connected",
                "This is a connectivity test from CryptoAI Terminal. If you see this message, SMTP credentials are working.")
            : Task.FromResult(false);

    private static bool IsValidEmail(string value) =>
        !string.IsNullOrWhiteSpace(value) && EmailPattern.IsMatch(value);

    public void Dispose() { /* SmtpClient is created per-send and disposed there */ }
}
