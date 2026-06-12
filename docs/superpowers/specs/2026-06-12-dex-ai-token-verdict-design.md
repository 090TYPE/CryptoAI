# Дизайн: AI-вердикт токена на DEX-странице (фича A)

> Дата: 2026-06-12 · Ветка: `feature/dex-ai-token-verdict`
> Часть пакета доработок DEX (A — AI-вердикт, B — свечи на DEX-графике, C — DEX-скринер).
> Это спека только для **A**. B и C — отдельные циклы.

## Цель

При выборе токена в панели **DEX Market** (страница Trading, venue = DEX) под деталями
токена показывается карточка AI-вердикта: насколько токен рискован для покупки.
Делает «AI» видимым прямо на торговой странице (принцип «AI до названия» из
`COMMERCIAL_ROADMAP.md`), переиспользуя уже написанную инфраструктуру безопасности.

## Поведение (согласовано)

1. **Авто-вердикт при выборе токена** — считается мгновенно из уже загруженных данных
   токена (ликвидность, объём, возраст, движение цены). Кэш по токену; живой вызов
   Claude/ChatGPT не чаще одного раза на токен.
2. **Кнопка «🔍 Deep scan»** — по клику до-тягивает on-chain безопасность
   (honeypot / налоги buy-sell / флаги) и уточняет вердикт.
3. Нет AI-ключа → детерминированная офлайн-эвристика с честной меткой «Heuristic (offline)».
   Всё асинхронно, UI не блокируется.

## Переиспользуемая инфраструктура (уже существует)

- `Services/TokenSecurityAiService.AssessAsync(DexTokenInfo, securitySummary?)` →
  `TokenAiVerdict { RiskScore 0–100, Verdict (AVOID/RISKY/NEUTRAL/FAVORABLE), RedFlags[],
  Reason, Source, IsFallback }`. Кэш по `chainId:tokenAddress`, офлайн-эвристика,
  ключ/модель из `AiRuntime`. Уже используется в снайпере.
- `Gateway.DEX/TokenSecurityService.ScanAsync(chain, address, ct)` →
  `TokenSecurityResult { IsHoneypot, BuyTax, SellTax, Flags[], Source }`.
  Источники: GoPlus + Honeypot.is (EVM), RugCheck (Solana). Бесплатные API, без ключей.
- `DexTradingViewModel.SelectedToken` (`DexTokenItemViewModel`) → `.TokenInfo` это уже
  `DexTokenInfo` — ровно вход для `AssessAsync`. `HasSelectedToken` уже есть.
- Образец UI/VM-паттерна карточки вердикта: `SniperCandidateViewModel`
  (`AiVerdict / AiVerdictBadge / AiReason / AiRedFlagsText / AiAccentHex`).

## Компоненты

### 1. `DexTokenAiVerdictViewModel` (новый, изолированный)
Маленький VM, держит состояние карточки. Одна ответственность: превратить
`TokenAiVerdict` в готовые для биндинга свойства и оркестрировать обновление.

Свойства:
- `bool HasVerdict`, `bool IsBusy`
- `int RiskScore`, `string ScoreLabel` (`"{score}/100"`)
- `string Badge` (AVOID/RISKY/NEUTRAL/FAVORABLE)
- `string AccentHex` (цвет по вердикту: AVOID `#FF6B6B`, RISKY `#F4B860`,
  NEUTRAL `#8FA3B8`, FAVORABLE `#21E6C1`)
- `IReadOnlyList<string> RedFlags`, `string RedFlagsText`
- `string Reason`
- `string SourceLabel` («AI · <model>» / «Heuristic (offline)» / «Deep: GoPlus + Honeypot.is»)
- `bool DeepScanBusy`, `string? DeepScanNote` (напр. «deep scan unavailable for this chain»)
- `ReactiveCommand DeepScanCommand`

Метод `void ApplyVerdict(TokenAiVerdict)` — заполняет свойства.

### 2. Оркестрация в `DexTradingViewModel`
- Конструктор получает **общий** `TokenSecurityAiService` (тот же инстанс, что у снайпера —
  общий кэш и ключ) из `MainWindowViewModel`.
- Владеет одним `TokenSecurityService` (IDisposable) для Deep scan; `Dispose` при закрытии VM.
- Новое свойство `DexTokenAiVerdictViewModel TokenVerdict { get; }`.
- В сеттере `SelectedToken`: запустить `RefreshVerdictAsync(token)` (fire-and-forget,
  `App.UiScheduler`, try/catch). Guard устаревших результатов через монотонный
  `_verdictSeq`: применяю результат, только если `SelectedToken` тот же и seq актуальный.
- `RefreshVerdictAsync`: `IsBusy=true` → `AssessAsync(tokenInfo)` → `ApplyVerdict` → `IsBusy=false`.
- `DeepScanCommand`: `DeepScanBusy=true` → `ScanAsync(chain, addr)` → `BuildSecuritySummary(result)`
  → `aiService.Invalidate(token)` → `AssessAsync(token, summary)` → `ApplyVerdict` (Source = deep)
  → `DeepScanBusy=false`. Ошибка/неподдерживаемая сеть → оставить лёгкий вердикт + `DeepScanNote`.

### 3. Чистая функция `BuildSecuritySummary(TokenSecurityResult) : string`
Складывает honeypot/налоги/флаги в строку, понятную эвристике/модели
(эвристика ищет ключевые слова «honeypot», «mintable», «blacklist»; строка также идёт
в промпт модели). Тестируется таблично, без сети.

### 4. UI-карточка (MainWindow.axaml, панель DEX Market, под деталями токена)
- Виден при `HasSelectedToken`.
- Цветной бейдж `Badge` (фон/обводка из `AccentHex`) + `ScoreLabel`.
- Чипы `RedFlags` (WrapPanel), `Reason` (wrap), строка `SourceLabel`.
- Кнопка «🔍 Deep scan» (спиннер по `DeepScanBusy`, скрыт/disabled во время скана),
  необязательная заметка `DeepScanNote`.
- Спиннер/плейсхолдер во время `IsBusy`.

## Поток данных

```
выбор токена → SelectedToken setter → RefreshVerdictAsync(token)
   → TokenSecurityAiService.AssessAsync(token.TokenInfo)         // мгновенно: кэш/эвристика, или живой вызов
   → DexTokenAiVerdictViewModel.ApplyVerdict(verdict)            // карточка заполнена

клик Deep scan → DeepScanCommand
   → TokenSecurityService.ScanAsync(chain, addr)                 // GoPlus/Honeypot.is/RugCheck
   → BuildSecuritySummary(result)
   → aiService.Invalidate(token) → AssessAsync(token, summary)   // уточнённый вердикт
   → ApplyVerdict(verdict)  (SourceLabel = «Deep: …»)
```

## Обработка краёв

- Нет выбранного токена → карточка скрыта.
- Быстрое переключение токенов → guard `_verdictSeq`: устаревший ответ отбрасывается.
- Deep scan: сеть упала / Tron не поддержан `ScanAsync` → лёгкий вердикт остаётся,
  показываем `DeepScanNote`. `TokenSecurityService` уже деградирует мягко.
- Нет AI-ключа → офлайн-эвристика, метка «Heuristic (offline)» (уже реализовано в сервисе).
- Повторный Deep scan того же токена — повторный `ScanAsync` (свежие данные), затем
  `Invalidate` + переоценка.
- Всё на `App.UiScheduler`; UI-поток не блокируется.

## Тесты

- `DexTokenAiVerdictViewModelTests` — `ApplyVerdict` маппит каждый `Verdict` в правильный
  `Badge`/`AccentHex`; `RedFlagsText` собирается; пустые red-flags обрабатываются.
- Guard устаревших результатов — смена токена не даёт старому вердикту перетереть новый
  (через инкремент seq).
- `BuildSecuritySummaryTests` — табличные: honeypot/налоги/флаги → ожидаемая строка
  (вкл. ветку «чисто»).
- Smoke: `dotnet build` зелёный; запуск в demo, выбор токена → карточка с эвристикой;
  клик Deep scan на EVM-токене → обновлённый источник (если есть сеть).

## Явно вне scope v1
- Предупреждение/блок на кнопке BUY при AVOID.
- Авто-Deep-scan при каждом выборе.
- История/лог вердиктов.

Эти пункты — кандидаты на отдельные итерации после v1.
