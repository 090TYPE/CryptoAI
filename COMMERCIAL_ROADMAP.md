# CryptoAI Terminal — Коммерческий Roadmap (v2)

> Цель документа: не «добавить ещё функций» (их уже больше, чем у конкурентов),
> а **довести продукт до состояния, в котором покупатель готов платить**.
> Фокус: AI-до-названия, доверие/демо, лицензирование, расширение рынка.
>
> Старый технический roadmap — `DEVELOPMENT_ROADMAP.md` (модули + реестр багов).
> Этот файл — про продажу. Обновлять по мере работы.

---

## Принцип приоритизации

Покупатель торгового софта принимает решение по трём осям:
1. **«Это реально AI?»** — продукт называется *Crypto AI Terminal*, AI должен быть виден.
2. **«Этому можно доверить деньги?»** — нужны демо, доказательства, прозрачность.
3. **«Это продукт, а не папка с кодом?»** — лицензия, онбординг, обновления, витрина.

Каждый EPIC ниже бьёт по одной из осей. Порядок = влияние на конверсию × скорость.

---

## EPIC A — «AI до названия» (ось 1, наивысший приоритет)

> Сейчас «AI» = вызов Claude API для buy/sell из 10 свечей. Покупатель ждёт AI как ядро.
> Эти задачи делают AI **видимым** в демо — это то, что продаёт.

### A1. AI-вердикт токена в снайпере ✅ ГОТОВО
**Что:** при принятии кандидата снайпер показывает AI-оценку риска токена.
**Видимый результат:** карточка кандидата получает блок
`AI: AVOID · risk 72/100` + причина + red flags + источник (Claude/Heuristic).

**Реализовано:**
- `CryptoAITerminal.Core/Models/TokenAiVerdict.cs` — модель вердикта.
- `CryptoAITerminal.AIEngine/TokenSecurityAiProvider.cs` — Claude-вызов (паттерн `ClaudeSignalProvider`).
- `CryptoAITerminal.TerminalUI/Services/TokenSecurityAiService.cs` — кэш по токену + таймаут + **офлайн-эвристика** (работает без ключа → виден в демо).
- `SniperCandidateViewModel.cs` — свойства `AiVerdict/AiVerdictBadge/AiReason/AiRedFlagsText/AiAccentHex/...`.
- `SniperViewModel.cs` — `StartAiVerdict`/`AssessCandidateAiAsync` (неблокирующий запуск при accept), флаг `EnableAiTokenVerdict`, `ConfigureAiVerdict(key,model)`.
- `MainWindowViewModel.cs` — Claude-ключ из AI Bot шарится в снайпер.
- `MainWindow.axaml` — блок AI-вердикта в карточке AcceptedPairs.
- Тесты: `TokenSecurityAiServiceTests.cs` (5, эвристика, все зелёные).

**Ключ:** env `ANTHROPIC_API_KEY` / `CRYPTOAI_CLAUDE_KEY`, либо поле Claude в AI Bot. Без ключа — офлайн-эвристика.

**Файлы:**
- `CryptoAITerminal.AIEngine/TokenSecurityAiProvider.cs` — новый. Вызов Claude (переиспользовать паттерн `ClaudeSignalProvider`).
- `CryptoAITerminal.Core/Models/TokenAiVerdict.cs` — новая модель (`int RiskScore`, `string Verdict`, `string[] RedFlags`, `string Reason`).
- `CryptoAITerminal.TerminalUI/Services/TokenSecurityAiService.cs` — обёртка с кэшем по адресу токена + таймаут/фолбэк.
- `SniperViewModel.RiskChecks.cs` — вызвать в `EvaluateRisk`, сложить как доп. фактор (не заменять hardcoded-проверки).
- `SniperCandidateViewModel.cs` — свойства `AiVerdict`, `AiRiskScore`, `AiRedFlagsText`.
- `MainWindow.axaml` — блок в карточке кандидата.

**Критерий готовности:** в paper-режиме видно AI-вердикт без реального API-ключа (фолбэк-заглушка), с ключом — реальный вызов. Не блокирует UI-поток.

### A2. AI-объяснение сделок бота ✅ ГОТОВО
**Что:** в `BotLog` каждый actionable BUY/SELL AI-стратегии сопровождается строкой `🧠 <reason>` — моделью объяснённое «почему».
**Реализовано:** `AIBotViewModel.cs` — типизированная ссылка `claudeStrategy`, в `OnSignal` добавляется `LastReason` (срабатывает только на BUY/SELL conf>0.3, т.е. прямо перед сделкой). `ClaudeStrategy.LastReason` уже существовал.

### A3. AI-резюме новостей + агрегированный sentiment ✅ ГОТОВО
**Что:** «AI Market Pulse» в секции News — агрегированный sentiment за час (детерминированный) + AI-дайджест рынка (одним вызовом Claude, офлайн-фолбэк).
**Видимый результат:** карточка вверху News Feed: бейдж pulse (Bullish/Bearish/…), счётчики `N▲ N▼ N●`, AI-резюме одной-двумя фразами + кнопка «↻ AI digest».

**Реализовано:**
- `AIEngine/NewsDigestAiProvider.cs` — один Claude-вызов по заголовкам → `{summary, bias}`.
- `TerminalUI/Services/NewsAiSummaryService.cs` — обёртка + **офлайн-дайджест** из tally (работает без ключа).
- `NewsFeedViewModel.cs` — `PulseScore/PulseLabel/PulseBrush/PulseDetail` (окно 1ч), `AiDigest/AiDigestBias/AiDigestBrush/AiDigestRunning`, `RefreshDigestCommand` (throttle 3 мин), `ConfigureAi`.
- `DashboardViewModel.cs` — зеркалит pulse+digest (на случай привязки overview-дашборда; сам overview в XAML сейчас не привязан, поэтому видимая поверхность — секция News).
- `MainWindow.axaml` — карточка AI Market Pulse в начале News Feed.
- `MainWindowViewModel.cs` — Claude-ключ из AI Bot шарится и в News; порядок создания NewsFeedVM→DashboardVM поправлен.
- Тесты: `NewsAiSummaryServiceTests.cs` (5). Полный сьют 122 зелёных.

**Примечание:** per-news построчная AI-суммаризация заменена на один дайджест (дешевле/быстрее, лучше для демо); per-news sentiment остаётся keyword-классификатором.

---

## EPIC B — Доверие и демо (ось 2)

> Без доказательств торговый софт не покупают. Здесь самые дешёвые шаги с большим эффектом.

### B1. Явный Paper / Demo режим «без API-ключей» ✅ ГОТОВО
**Что:** первый запуск показывает welcome-оверлей «You're in Demo (Paper) mode», объясняющий что всё симулируется и ключи не нужны.
**База:** backbone уже есть — `WalletWorkspaceViewModel.GlobalPaperOnlyMode = true` по умолчанию блокирует любое live-исполнение.

**Реализовано:**
- `MainWindowViewModel.cs` — `IsWelcomeVisible`, `IsFirstRun()`/`MarkWelcomeShown()` (маркер `%LOCALAPPDATA%/CryptoAITerminal/.welcome-shown`), команды `StartDemoCommand` / `OpenApiKeysFromWelcomeCommand`.
- `MainWindow.axaml` — welcome-оверлей (ZIndex 200) с бейджем «DEMO · NO API KEYS NEEDED», списком возможностей и двумя кнопками. Splash поднят на ZIndex 300, чтобы анимация запуска шла поверх.
- Кнопка «Add API keys» ведёт в Settings; «Start exploring in Demo» закрывает оверлей и фиксирует paper-режим.
- Проверка: сборка чистая, smoke-run 10с без падений, маркер появляется только после явного закрытия.

### B2. Витрина в README (скриншоты + видео)
**Что:** раздел «Скриншот» сейчас пустой. Добавить 4–6 скриншотов разделов + splash-видео из `design/splash-intro/`.
**Эффект:** покупатель решает «нравится/нет» глазами за 10 секунд.

### B3. Экспортируемый отчёт бэктеста ✅ ГОТОВО
**Что:** кнопка «Export Report» рендерит самодостаточный **HTML-отчёт** (inline-SVG equity-кривая + buy&hold, KPI-плитки, таблица сравнения стратегий, Monte Carlo, брендинг) и открывает его в браузере. Печать в PDF — Ctrl+P.
**Решение:** HTML вместо PDF-библиотеки — ноль новых зависимостей (в духе проекта), максимально шаринговый формат, печатается в PDF силами браузера.

**Реализовано:**
- `Services/BacktestReportExporter.cs` — `BuildHtml(ReportModel)`, inline-SVG график (strategy + buy&hold + baseline 100%), `@media print` стили, HTML-эскейпинг.
- `BacktestViewModel.cs` — `ExportReportCommand` + `ExportReport()` (собирает модель из `_mainResult`/`ComparisonRows`/`_monteCarloSummary`/`_buyHoldValues`, пишет `*_report.html` в `Documents/CryptoAITerminal/Backtests/`, открывает через `Process.Start UseShellExecute`). Сохранён `_buyHoldValues` при построении графика.
- `MainWindow.axaml` — кнопка «📄 Export Report» рядом с «Export CSV».
- Тесты: `BacktestReportExporterTests.cs` (4: структура, метрики/SVG, эскейпинг, мало точек). Полный сьют 136 зелёных.

---

## EPIC C — Коммерциализация (ось 3)

> Превращает «папку с кодом» в продаваемый продукт.

### C1. Onboarding-визард первого запуска ✅ ГОТОВО
Выбор биржи → ввод ключей → безопасное сохранение (DPAPI) → «restart to apply». Открывается из welcome-оверлея кнопкой «Add API keys».

**Реализовано:**
- `ViewModels/OnboardingViewModel.cs` — 3 шага (ChooseExchange/EnterKeys/Done), `NeedsPassphrase` (OKX/KuCoin), валидация, сохранение через `CredentialsService.SaveX`, очистка секретов из памяти после сохранения.
- `MainWindow.axaml` — overlay-визард (ZIndex 210) с шагами, маскированным вводом (`PasswordChar`), ссылками на безопасность.
- `MainWindowViewModel.cs` — `OnboardingVM`; welcome «Add API keys» теперь открывает визард (а не Settings).
- Тесты: `OnboardingViewModelTests.cs` (навигация + валидация, без записи на диск). Полный сьют 132 зелёных.

**Сознательно без «живого теста соединения»:** Binance spot-`GetBalanceAsync` — заглушка, Bybit/OKX глотают auth-ошибки → ложный «✓ Connected» подорвал бы доверие. Вместо этого честное «Keys saved securely (DPAPI). Restart to connect.» Живой тест — кандидат на будущее (нужны прямые signed REST-вызовы на биржу).

### C2. Лицензирование / активация ✅ ГОТОВО
Офлайн-валидация по RSA-подписи: приложение содержит публичный ключ, лицензии подписываются приватным (у продавца). Триал 14 дней, привязка к машине (опционально), enforcement live-торговли.

**Реализовано:**
- `Services/LicenseService.cs` — встроенный RSA-pubkey; токен `base64url(payloadJson).base64(sig)` (RSA-SHA256). `Validate` (Valid/BadFormat/BadSignature/Expired/WrongMachine), `TryActivate` (persist в `license.key`), `GetSnapshot` (Trial/Licensed/Expired), `TrialDaysRemaining` (маркер `.trial`), `GetMachineId` (SHA256 от machine/user/os), `CreateToken` (seller-side подпись).
- `ViewModels/LicenseViewModel.cs` — статус + активация, событие `LicenseChanged`.
- `WalletWorkspaceViewModel.cs` — `LicenseAllowsLive`; `GetExecutionGuardBlockReason`/`ApplyGlobalExecutionMode` блокируют live без лицензии (демо всегда доступно).
- `MainWindowViewModel.cs` — `LicenseVM`, `ApplyLicenseState` (expired → pin paper-only + block live), авто-открытие активации при истёкшем триале.
- `MainWindow.axaml` — чип статуса (top-right) + overlay активации (ZIndex 220, показывает Machine ID).
- Ключи: приватный в `.license-signing/private.pem` (**gitignored**), публичный встроен. Выпуск лицензий: `tools/issue-license.sh "Name" [edition] [expiresISO|none] [machineId|none]`.
- Тесты: `LicenseServiceTests.cs` (15, вкл. регрессию «встроенный pubkey валидирует seller-подписанный токен»). Полный сьют 163 зелёных.

**Как выпустить лицензию:** `bash tools/issue-license.sh "Acme Trading" Pro 2027-01-01T00:00:00Z` → выдаёт ключ для вставки в диалог активации. Machine ID клиента виден в этом же диалоге (для привязки).

### C4. Telegram-бот продажи лицензий ✅ ГОТОВО
Отдельный проект `CryptoAITerminal.LicenseBot` (.NET console, Telegram.Bot 22.6 + SQLite). Продаёт лицензии за **Telegram Stars** (встроенная оплата, без внешнего провайдера), подписывает ключ тем же приватным ключом и мгновенно выдаёт его клиенту. Хранит БД клиентов и заказов.

**Реализовано:**
- `LicenseSigner.cs` — тот же формат токена, что валидирует терминал (кросс-тест `LicenseBotCompatibilityTests` это гарантирует).
- `Store.cs` — SQLite: `customers` (tg id, username, имя), `orders` (план, редакция, Stars, charge id, ключ, expiry, статус).
- `BotConfig.cs` — конфиг (token, приватный ключ, админы, тарифы) из appsettings.json/env. Тарифы `DefaultPlans` (Lite/Pro · месяц/год/lifetime), цены в ⭐ и ₽ (Pro·месяц = **2000 ₽** / 600 ⭐).
- `UpdateHandler.cs` — `/buy` (две кнопки на тариф: ⭐ и крипто) → Stars: SendInvoice(XTR)→PreCheckout→SuccessfulPayment; крипто: создание счёта→фоновый поллинг→ключ. `/mykeys`; админ `/stats`, `/recent`, `/issue`.
- `CryptoPayClient.cs` — оплата криптой через **Crypto Pay API** (@CryptoBot): счёт в фиате (RUB), клиент платит крипто-эквивалент (USDT/TON), бот поллит статус и выдаёт ключ. Включается переменной `CRYPTOPAY_TOKEN`.
- `Program.cs` — long-polling, валидация запуска.
- `appsettings.example.json`, `README.md`. БД и реальный appsettings — в .gitignore.
- Тесты: `LicenseBotCompatibilityTests.cs` (3). Полный сьют 167 зелёных.

**Запуск:** `BOT_TOKEN=… LICENSE_PRIVATE_KEY_PATH=../.license-signing/private.pem BOT_ADMIN_IDS=… dotnet run --project CryptoAITerminal.LicenseBot`

### C3. Авто-обновления ✅ ГОТОВО
Проверка версии через GitHub Releases API на старте → ненавязчивый баннер «Update available» с кнопкой Download (открывает страницу релиза).

**Реализовано:**
- `Services/UpdateCheckService.cs` — `AppInfo` (Version `1.0.0`, RepoSlug `090TYPE/CryptoAI`), `CheckAsync` (GET releases/latest, парс `tag_name`/`html_url`), чистая `IsNewer(current,latest)` (терпит `v`-префикс, pre-release суффиксы; unparseable → false, fail-safe). Любые сетевые/парс-ошибки не фатальны.
- `MainWindowViewModel.cs` — `StartUpdateCheck()` на старте (фоном), `IsUpdateAvailable`/`UpdateBannerText`, команды `OpenUpdateCommand`/`DismissUpdateCommand`.
- `MainWindow.axaml` — баннер внизу слева с «Later»/«Download».
- Тесты: `UpdateCheckServiceTests.cs` (13). Полный сьют 149 зелёных.

---

## EPIC D — Расширение рынка (ось 3, позже)

### D1. Мобильное приложение поверх WebApi (push + «закрыть позицию»)
### D2. Кросс-платформенный publish (macOS/Linux — Avalonia уже умеет, только pubxml)
### D3. Marketplace/шаринг конфигов (профили уже есть — добавить импорт/экспорт)

---

## Порядок работы

| # | Задача | EPIC | Эффект | Сложность | Статус |
|---|--------|------|--------|-----------|--------|
| 1 | AI-вердикт токена в снайпере | A1 | ★★★ | Средняя | ✅ готово |
| 2 | Paper/Demo режим на первом экране | B1 | ★★★ | Низкая | ✅ готово |
| 3 | Витрина README (скрины+видео) | B2 | ★★★ | Низкая | — |
| 4 | AI-резюме новостей + sentiment | A3 | ★★ | Средняя | ✅ готово |
| 5 | Onboarding-визард | C1 | ★★ | Средняя | ✅ готово |
| 6 | Экспорт отчёта бэктеста | B3 | ★★ | Средняя | ✅ готово |
| 7 | AI-объяснение сделок бота | A2 | ★★ | Низкая | ✅ готово |
| 8 | Лицензирование | C2 | ★★ | Высокая | ✅ готово |
| 9 | Авто-обновления | C3 | ★ | Средняя | ✅ готово |
| 10 | Мобайл / кросс-платформа / marketplace | D | ★ | Высокая | — |

---

## Контекст для следующей сессии
Перед работой читать: этот файл → `DEVELOPMENT_ROADMAP.md` (карта модулей) → конкретный файл.
Стартовая задача — **A1 (AI-вердикт токена)**: даёт видимый «AI» в демо при минимуме риска для существующего кода.
