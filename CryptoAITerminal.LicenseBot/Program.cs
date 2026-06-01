using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using CryptoAITerminal.LicenseBot;

var cfg = BotConfig.Load();

if (string.IsNullOrWhiteSpace(cfg.BotToken))
{
    Console.Error.WriteLine("BOT_TOKEN is not set. Put it in appsettings.json or the BOT_TOKEN env var.");
    return 1;
}
if (string.IsNullOrWhiteSpace(cfg.PrivateKeyPem))
{
    Console.Error.WriteLine("License private key missing. Set LICENSE_PRIVATE_KEY_PATH (PEM) or LICENSE_PRIVATE_KEY.");
    return 1;
}

var store = new Store(cfg.DbPath);
var bot = new TelegramBotClient(cfg.BotToken);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var crypto = cfg.CryptoEnabled
    ? new CryptoPayClient(cfg.CryptoPayToken, cfg.CryptoPayAssets, cfg.CryptoPayApiBase)
    : null;
var handler = new UpdateHandler(bot, cfg, store, crypto, cts.Token);

var me = await bot.GetMe(cts.Token);
Console.WriteLine($"License bot @{me.Username} started. Plans: {cfg.Plans.Count}. Admins: {cfg.AdminIds.Length}. " +
    $"Stars: {cfg.Currency}. Crypto: {(cfg.CryptoEnabled ? $"on ({cfg.CryptoFiat}, {cfg.CryptoPayAssets})" : "off")}.");
Console.WriteLine("Press Ctrl+C to stop.");

int offset = 0;
var allowed = new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery };

while (!cts.IsCancellationRequested)
{
    try
    {
        var updates = await bot.GetUpdates(
            offset: offset,
            timeout: 30,
            allowedUpdates: allowed,
            cancellationToken: cts.Token);

        foreach (var update in updates)
        {
            offset = update.Id + 1;
            try
            {
                await handler.HandleAsync(update, cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[update {update.Id}] {ex.Message}");
            }
        }
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[poll] {ex.Message}");
        try { await Task.Delay(3000, cts.Token); } catch { break; }
    }
}

Console.WriteLine("Stopped.");
return 0;
