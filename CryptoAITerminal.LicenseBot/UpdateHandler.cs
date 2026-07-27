using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

namespace CryptoAITerminal.LicenseBot;

/// <summary>
/// Routes Telegram updates: catalog/buy commands, the Stars payment flow
/// (invoice → pre-checkout → successful payment → signed license key), customer
/// self-service, and admin tools.
/// </summary>
public sealed class UpdateHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly BotConfig _cfg;
    private readonly Store _store;
    private readonly CryptoPayClient? _crypto;
    private readonly CancellationToken _appCt;

    // Machine ID shown in the terminal's Activate dialog: 16 hex chars.
    private static readonly Regex MachineIdRegex = new("^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);
    // telegramId → "stars:<code>" | "crypto:<code>" while we wait for their Machine ID.
    private readonly ConcurrentDictionary<long, string> _awaitingMachine = new();

    public UpdateHandler(ITelegramBotClient bot, BotConfig cfg, Store store,
        CryptoPayClient? crypto = null, CancellationToken appCt = default)
    {
        _bot = bot;
        _cfg = cfg;
        _store = store;
        _crypto = crypto;
        _appCt = appCt;
    }

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        switch (update.Type)
        {
            case UpdateType.Message when update.Message!.SuccessfulPayment is { } sp:
                await OnSuccessfulPayment(update.Message, sp, ct);
                break;
            case UpdateType.Message when update.Message!.Text is { } text:
                await OnCommand(update.Message, text.Trim(), ct);
                break;
            case UpdateType.CallbackQuery:
                await OnCallback(update.CallbackQuery!, ct);
                break;
            case UpdateType.PreCheckoutQuery:
                await OnPreCheckout(update.PreCheckoutQuery!, ct);
                break;
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private async Task OnCommand(Message msg, string text, CancellationToken ct)
    {
        var from = msg.From;
        if (from is not null)
            _store.UpsertCustomer(from.Id, from.Username, FullName(from));

        // A bare Machine ID (16 hex) — save it and resume any pending purchase.
        if (from is not null && MachineIdRegex.IsMatch(text))
        {
            await OnMachineIdReceived(msg.Chat.Id, from, text.ToUpperInvariant(), ct);
            return;
        }

        var cmd = text.Split(' ', 2)[0].ToLowerInvariant();
        // strip @botname suffix in groups
        var at = cmd.IndexOf('@');
        if (at >= 0) cmd = cmd[..at];

        switch (cmd)
        {
            case "/start":
            case "/help":
                await Reply(msg.Chat.Id,
                    "*CryptoAI Terminal — бот лицензий*\n" +
                    "Покупка подписки и мгновенная выдача ключа. Ключ *привязывается к вашему компьютеру*.\n\n" +
                    "*Как купить — 4 шага:*\n" +
                    "1️⃣ Откройте терминал → *Настройки → Лицензия* → скопируйте *Machine ID* (16 символов).\n" +
                    "2️⃣ Пришлите этот Machine ID сюда обычным сообщением.\n" +
                    "3️⃣ Команда /buy → выберите тариф → оплата криптовалютой.\n" +
                    "4️⃣ Я пришлю ключ → вставьте его в терминале (*Настройки → Лицензия*) → *Активировать*.\n\n" +
                    "*Команды:*\n" +
                    "/buy — тарифы и покупка\n" +
                    "/mykeys — ваши ключи\n" +
                    "/mymachine — сохранённый Machine ID\n" +
                    "/bind `<MachineID>` — задать или сменить Machine ID\n" +
                    "/help — это сообщение\n\n" +
                    "ℹ️ Где взять Machine ID: в терминале, *Настройки → Лицензия* (или окно активации). " +
                    "Без ключа после 14 дней пробного периода остаётся *бумажный режим*.", ct);
                break;

            case "/buy":
                await ShowPlans(msg.Chat.Id, ct);
                break;

            case "/mykeys":
                await ShowMyKeys(msg.Chat.Id, from?.Id ?? 0, ct);
                break;

            case "/mymachine":
                var mid = _store.GetCustomerMachine(from?.Id ?? 0);
                await Reply(msg.Chat.Id, mid is null
                    ? "Machine ID пока не сохранён.\nОткройте терминал → *Настройки → Лицензия*, скопируйте 16-значный *Machine ID* и пришлите его сюда."
                    : $"Ваш Machine ID: `{mid}`\nСменить — командой /bind.", ct);
                break;

            case "/bind":
                var arg = text.Split(' ', 2);
                if (arg.Length == 2 && MachineIdRegex.IsMatch(arg[1].Trim()) && from is not null)
                    await OnMachineIdReceived(msg.Chat.Id, from, arg[1].Trim().ToUpperInvariant(), ct);
                else
                    await Reply(msg.Chat.Id, "Формат: `/bind <MachineID>` — 16 символов из окна активации терминала.", ct);
                break;

            // ── Админские ──
            case "/stats" when _cfg.IsAdmin(from?.Id ?? 0):
                var (cust, paid, collected) = _store.Stats();
                await Reply(msg.Chat.Id, $"*Статистика*\nКлиентов: {cust}\nОплаченных заказов: {paid}\nСобрано: {collected}", ct);
                break;

            case "/recent" when _cfg.IsAdmin(from?.Id ?? 0):
                await ShowRecent(msg.Chat.Id, ct);
                break;

            case "/issue" when _cfg.IsAdmin(from?.Id ?? 0):
                await AdminIssue(msg, text, ct);
                break;

            default:
                if (cmd.StartsWith('/'))
                    await Reply(msg.Chat.Id, "Не знаю такой команды. Попробуйте /buy или /help.", ct);
                break;
        }
    }

    private async Task ShowPlans(ChatId chat, CancellationToken ct)
    {
        var cryptoOn = _crypto?.IsConfigured == true;
        if (!cryptoOn)
        {
            // Said plainly rather than shown as an empty plan list. Without a payment channel the
            // bot cannot sell anything, and a customer staring at buttons that do nothing is worse
            // than one who is told to come back.
            await Reply(chat, "Оплата сейчас недоступна — приём платежей не настроен. Напишите администратору.", ct);
            return;
        }

        var kb = new InlineKeyboardMarkup(_cfg.Plans.Select(p => new[]
        {
            InlineKeyboardButton.WithCallbackData($"{p.Title} — ${p.UsdPrice:0.##}", $"cpay:{p.Code}")
        }));

        var sb = new StringBuilder("*Выберите тариф*\n\n");
        foreach (var p in _cfg.Plans)
            sb.Append($"• *{p.Title}* — ${p.UsdPrice:0.##} (≈ {p.UsdPrice * _cfg.UsdRubRate:0} ₽)\n  {p.Description}\n");

        sb.Append("\nОплата криптовалютой (USDT, TON). Ключ приходит сразу после подтверждения платежа.");

        await _bot.SendMessage(chat, sb.ToString(), parseMode: ParseMode.Markdown,
            replyMarkup: kb, cancellationToken: ct);
    }

    private async Task ShowMyKeys(ChatId chat, long telegramId, CancellationToken ct)
    {
        var orders = _store.GetOrders(telegramId).Where(o => o.Status == "paid").ToList();
        if (orders.Count == 0)
        {
            await Reply(chat, "Ключей пока нет. Купить — команда /buy.", ct);
            return;
        }

        foreach (var o in orders.Take(10))
        {
            var exp = o.Expires is { } e ? e.ToString("yyyy-MM-dd") : "perpetual";
            await _bot.SendMessage(chat,
                $"*{o.Edition}* · expires {exp}\n`{o.LicenseKey}`",
                parseMode: ParseMode.Markdown, cancellationToken: ct);
        }
    }

    private async Task ShowRecent(ChatId chat, CancellationToken ct)
    {
        var orders = _store.RecentOrders(15);
        if (orders.Count == 0) { await Reply(chat, "Заказов пока нет.", ct); return; }
        var sb = new StringBuilder("*Recent orders*\n");
        foreach (var o in orders)
            sb.Append($"#{o.Id} tg:{o.TelegramId} {o.Edition} {o.Stars}⭐ {o.Status} {o.CreatedUtc:MM-dd HH:mm}\n");
        await Reply(chat, sb.ToString(), ct);
    }

    // /issue <edition> <days|0> <machineId|none> <Name...>  → manual key (no payment)
    private async Task AdminIssue(Message msg, string text, CancellationToken ct)
    {
        var parts = text.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || !int.TryParse(parts[2], out var days))
        {
            await Reply(msg.Chat.Id,
                "Usage: `/issue <edition> <days|0> <machineId|none> <customer name>`\n" +
                "e.g. `/issue Pro 365 none Acme Trading` or `/issue Pro 0 A1B2C3D4E5F6A7B8 Bob`", ct);
            return;
        }

        var edition = parts[1];
        var machineArg = parts[3];
        var machine = machineArg.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : machineArg.ToUpperInvariant();
        var name = parts[4];
        var expires = days > 0 ? DateTime.UtcNow.AddDays(days) : (DateTime?)null;
        var key = LicenseSigner.CreateToken(new LicenseInfo(name, edition, expires, machine, DateTime.UtcNow), _cfg.PrivateKeyPem);

        _store.AddOrder(new OrderRow(0, msg.From!.Id, "admin", edition, 0, "MANUAL", null, key,
            expires, machine, "paid", DateTime.UtcNow));

        var bound = machine is null ? "без привязки" : $"привязан к `{machine}`";
        await _bot.SendMessage(msg.Chat.Id,
            $"Issued *{edition}* for *{name}* ({bound}):\n`{key}`", parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    // ── Purchase flow ────────────────────────────────────────────────────────

    private async Task OnCallback(CallbackQuery cq, CancellationToken ct)
    {
        var data = cq.Data ?? "";

        // "buy:" is still accepted: it is what the Stars buttons used, and a customer can have an
        // old message with those buttons sitting in their chat history. Routing it to the crypto
        // flow is better than answering "unknown action" to someone trying to pay us.
        if ((data.StartsWith("cpay:", StringComparison.Ordinal) || data.StartsWith("buy:", StringComparison.Ordinal))
            && cq.Message is { } cm)
        {
            var code = data[(data.IndexOf(':') + 1)..];
            await _bot.AnswerCallbackQuery(cq.Id, cancellationToken: ct);

            if (_cfg.FindPlan(code) is null)
            {
                // The old tiers are gone, so a button from a stale message names a plan that no
                // longer exists. Show the current list instead of failing silently.
                await Reply(cm.Chat.Id, "Этот тариф больше не продаётся — вот актуальные:", ct);
                await ShowPlans(cm.Chat.Id, ct);
                return;
            }

            if (RequireMachineId(cm.Chat.Id, cq.From.Id, "crypto", code, out _))
                await Reply(cm.Chat.Id, "Сначала пришлите ваш *Machine ID*.\nОн в терминале: *Настройки → Лицензия*, 16 символов. Пришлите его сюда, и я выставлю счёт.", ct);
            else
                await StartCryptoInvoice(cm.Chat.Id, cq.From.Id, FullName(cq.From), code, ct);
        }
    }

    /// <summary>
    /// True (and records a pending purchase) when the customer hasn't bound a
    /// Machine ID yet, so the caller should ask for it before charging.
    /// </summary>
    private bool RequireMachineId(ChatId chat, long telegramId, string method, string code, out string? machineId)
    {
        machineId = _store.GetCustomerMachine(telegramId);
        if (machineId is not null) return false;
        _awaitingMachine[telegramId] = $"{method}:{code}";
        return true;
    }

    private async Task OnMachineIdReceived(ChatId chat, User from, string machineId, CancellationToken ct)
    {
        _store.SetCustomerMachine(from.Id, machineId);
        await Reply(chat, $"✅ Machine ID сохранён: `{machineId}`\nКупленные ключи будут привязаны к этому компьютеру.", ct);

        // Возобновляем отложенную покупку, если она была.
        if (_awaitingMachine.TryRemove(from.Id, out var pending))
        {
            var code = pending[(pending.IndexOf(':') + 1)..];
            if (_cfg.FindPlan(code) is null) return;
            await StartCryptoInvoice(chat, from.Id, FullName(from), code, ct);
        }
    }

    // ── Оплата криптовалютой (Crypto Pay API) ────────────────────────────────

    private async Task StartCryptoInvoice(ChatId chat, long telegramId, string name, string code, CancellationToken ct)
    {
        var plan = _cfg.FindPlan(code);
        if (plan is null) { await Reply(chat, "Такого тарифа нет.", ct); return; }

        if (_crypto?.IsConfigured != true)
        {
            await Reply(chat, "Приём платежей не настроен. Напишите администратору.", ct);
            return;
        }

        CryptoInvoice? inv;
        try
        {
            // Invoiced in the currency the price list is written in, so the amount charged cannot
            // drift away from the advertised price when the exchange rate moves.
            inv = await _crypto.CreateFiatInvoiceAsync(
                plan.UsdPrice, _cfg.CryptoFiat,
                $"CryptoAI Terminal — {plan.Title}",
                payload: $"plan:{plan.Code}", ct: ct);
        }
        catch (Exception ex)
        {
            await Reply(chat, $"Не получилось выставить счёт: {ex.Message}", ct);
            return;
        }
        if (inv is null) { await Reply(chat, "Не получилось выставить счёт. Попробуйте позже.", ct); return; }

        // Pending order keyed to the invoice id (kept in charge_id) for reconciliation.
        var orderId = _store.AddOrder(new OrderRow(
            0, telegramId, plan.Code, plan.Edition, 0, _cfg.CryptoFiat, inv.InvoiceId.ToString(), "",
            plan.Days > 0 ? DateTime.UtcNow.AddDays(plan.Days) : null, null, "pending", DateTime.UtcNow));

        var kb = new InlineKeyboardMarkup(InlineKeyboardButton.WithUrl($"Оплатить ${plan.UsdPrice:0.##}", inv.PayUrl));
        await _bot.SendMessage(chat,
            $"*{plan.Title}* — ${plan.UsdPrice:0.##} (≈ {plan.UsdPrice * _cfg.UsdRubRate:0} ₽)\n\n" +
            "Нажмите кнопку и оплатите эквивалент в USDT или TON. " +
            "Ключ придёт сюда автоматически, как только платёж подтвердится.",
            parseMode: ParseMode.Markdown, replyMarkup: kb, cancellationToken: ct);

        _ = PollCryptoInvoice(chat, telegramId, name, plan, orderId, inv.InvoiceId);
    }

    private async Task PollCryptoInvoice(ChatId chat, long telegramId, string name, Plan plan, long orderId, long invoiceId)
    {
        var deadline = DateTime.UtcNow.AddMinutes(35);
        try
        {
            while (DateTime.UtcNow < deadline && !_appCt.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(12), _appCt);

                var status = await _crypto!.GetInvoiceStatusAsync(invoiceId, _appCt);
                if (status == "paid")
                {
                    var order = _store.GetOrder(orderId);
                    if (order is null || order.Status == "paid") return; // already handled

                    var machine = _store.GetCustomerMachine(telegramId);
                    var expires = plan.Days > 0 ? DateTime.UtcNow.AddDays(plan.Days) : (DateTime?)null;
                    var key = LicenseSigner.CreateToken(
                        new LicenseInfo(name, plan.Edition, expires, machine, DateTime.UtcNow), _cfg.PrivateKeyPem);

                    _store.MarkOrderPaid(orderId, key);
                    var exp = expires is { } e ? e.ToString("yyyy-MM-dd") : "perpetual";
                    var bound = machine is null ? "" : $"\nBound to machine `{machine}`.";
                    await _bot.SendMessage(chat,
                        $"✅ *Crypto payment confirmed — thank you!*\n\n" +
                        $"*{plan.Edition}* license · expires {exp}{bound}\n\n" +
                        "Ваш ключ активации (нажмите, чтобы скопировать):\n" +
                        $"`{key}`\n\nOpen the terminal → *Activate* → paste the key.",
                        parseMode: ParseMode.Markdown, cancellationToken: _appCt);
                    return;
                }
                if (status == "expired") return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[crypto poll {invoiceId}] {ex.Message}");
        }
    }

    private async Task OnPreCheckout(PreCheckoutQuery q, CancellationToken ct)
    {
        var code = q.InvoicePayload.StartsWith("plan:", StringComparison.Ordinal)
            ? q.InvoicePayload["plan:".Length..]
            : "";
        var plan = _cfg.FindPlan(code);

        if (plan is null)
            await _bot.AnswerPreCheckoutQuery(q.Id, "Этот тариф больше не продаётся.", cancellationToken: ct);
        else
            await _bot.AnswerPreCheckoutQuery(q.Id, errorMessage: null, cancellationToken: ct); // approve
    }

    private async Task OnSuccessfulPayment(Message msg, SuccessfulPayment sp, CancellationToken ct)
    {
        var from = msg.From!;
        var code = sp.InvoicePayload.StartsWith("plan:", StringComparison.Ordinal)
            ? sp.InvoicePayload["plan:".Length..]
            : "";
        var plan = _cfg.FindPlan(code);
        if (plan is null)
        {
            await Reply(msg.Chat.Id, "Платёж получен, но тариф не найден. Напишите администратору.", ct);
            return;
        }

        var name = FullName(from);
        var machine = _store.GetCustomerMachine(from.Id);
        var expires = plan.Days > 0 ? DateTime.UtcNow.AddDays(plan.Days) : (DateTime?)null;
        var key = LicenseSigner.CreateToken(
            new LicenseInfo(name, plan.Edition, expires, machine, DateTime.UtcNow), _cfg.PrivateKeyPem);

        _store.UpsertCustomer(from.Id, from.Username, name);
        _store.AddOrder(new OrderRow(
            0, from.Id, plan.Code, plan.Edition, sp.TotalAmount, sp.Currency,
            sp.TelegramPaymentChargeId, key, expires, machine, "paid", DateTime.UtcNow));

        var exp = expires is { } e ? e.ToString("yyyy-MM-dd") : "perpetual";
        var bound = machine is null ? "" : $"\nBound to machine `{machine}`.";
        await _bot.SendMessage(msg.Chat.Id,
            $"✅ *Payment received — thank you!*\n\n" +
            $"*{plan.Edition}* license · expires {exp}{bound}\n\n" +
            "Ваш ключ активации (нажмите, чтобы скопировать):\n" +
            $"`{key}`\n\n" +
            "Откройте терминал → *Активировать* → вставьте ключ.",
            parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task Reply(ChatId chat, string text, CancellationToken ct) =>
        _bot.SendMessage(chat, text, parseMode: ParseMode.Markdown, cancellationToken: ct);

    private static string FullName(User u) =>
        string.Join(' ', new[] { u.FirstName, u.LastName }.Where(s => !string.IsNullOrWhiteSpace(s)))
            is { Length: > 0 } n ? n : (u.Username ?? $"tg{u.Id}");
}
