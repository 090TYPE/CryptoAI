# CryptoAI Terminal — Remaining improvements (handoff)

> Хендофф для новой сессии. Две крупные задачи, каждая требует решения и собственного
> цикла brainstorm → spec → plan → implement (см. `docs/superpowers/specs|plans`).
> Обе — на работающем `main`; делать на отдельной ветке, проверять сборкой + тестами + smoke.

## Контекст репозитория
- Корень: `C:\Users\090\Documents\GitHub\CryptoAI` (git, `main`, remote `origin` = github.com/090TYPE/CryptoAI).
- UI-проект: `CryptoAITerminal.TerminalUI`. Почти весь UI — `Views/MainWindow.axaml` (~11.5k строк) + вынесенный `Views/TradingDeskView.axaml` (вкладка Trading).
- Тесты: `CryptoAITerminal.Core.Tests` (xUnit), ~290. Паттерн проекта: чистую логику выносить в `Services/*.cs` статические классы и покрывать xUnit-тестами (примеры: `DexTokenFilter`, `DexCandleBuilder`, `DexRefreshPolicy`, `DexSecuritySummary`).
- Сборка: `dotnet build CryptoAITerminal.TerminalUI/CryptoAITerminal.TerminalUI.csproj -c Debug`
- Запуск: `bin\Debug\net8.0-windows\win-x64\CryptoAITerminal.TerminalUI.exe`
- ВАЖНО: перед `dotnet build`/`test` закрывать приложение, иначе сборка падает на залоченных DLL:
  `Get-Process | Where-Object { $_.ProcessName -like "*CryptoAI*" } | Stop-Process -Force -ErrorAction SilentlyContinue`

---

## Задача 1 — Унификация языка UI (RU/EN)

### Симптом
По интерфейсу смешаны русский и английский тексты; переключатель EN/RU (верх-право) переводит не всё.

### Как устроена локализация (важно понять до правок)
- `Services/UiLocalizationService.cs`: `enum UiLanguage { English, Russian }`, `CurrentLanguage`,
  `SetLanguage`, событие `LanguageChanged`, и `Translate(string englishText)` — ищет перевод в
  **большом словаре EN→RU** (строки ~14–660). При English возвращает текст как есть.
- `Views/MainWindow.axaml.cs`: таймер `_localizationScanTimer` (каждые 2с) **сканирует визуальное
  дерево**, регистрирует текстовые свойства `TextBlock/Button/TabItem/Expander`, запоминает
  исходный (английский) текст и при RU применяет `Translate(...)`. Перезапуск скана на
  `LanguageChanged` и смене раздела.

**Вывод:** модель — «авторим по-английски, в рантайме переводим в RU по словарю».
Значит «смесь» возникает, когда строка:
1. захардкожена по-русски в XAML/VM (при EN остаётся русской; и её нет как ключа в словаре), ИЛИ
2. английская, но **отсутствует в словаре** `UiLocalizationService` (при RU остаётся английской), ИЛИ
3. не попадает под скан (тип контрола/свойство не регистрируется).

### Решение, которое нужно принять
Подтвердить модель «английский — источник, RU — через словарь» (рекомендуется, т.к. инфраструктура
уже такая). Тогда задача = привести всё к этой модели.

### Шаги
1. **Найти русские литералы** в XAML/VM (они ломают модель):
   `rg -n "[А-Яа-яЁё]" CryptoAITerminal.TerminalUI/Views CryptoAITerminal.TerminalUI/ViewModels`
   (комментарии `<!-- -->` и `//` — пропускать; интересуют `Text=`, `Content=`, `PlaceholderText=`,
   `ToolTip.Tip=`, строковые литералы в VM, идущие в UI).
2. Перевести найденные русские литералы **на английский** в коде (источник — английский).
3. Для каждой английской строки убедиться, что в словаре `UiLocalizationService` есть RU-перевод;
   добавить недостающие пары.
4. Проверить охват скана: если какие-то контролы/свойства с текстом не переводятся — расширить
   регистрацию в `MainWindow.axaml.cs` (`RunLocalizationScanTick` и хелперы регистрации).
5. VM-строки, уходящие в UI (статусы, лейблы): тоже авторить по-английски + добавить в словарь.

### Файлы
`Services/UiLocalizationService.cs` (словарь + Translate), `Views/MainWindow.axaml.cs` (сканер),
`Views/MainWindow.axaml`, `Views/TradingDeskView.axaml`, многие `ViewModels/*.cs`.

### Gotchas
- Скан переводит по **исходному английскому** тексту → русские литералы переводить нельзя (нет ключа).
- `Translate` переводит построчно (`TranslateSingleLine`) — многострочные строки ок.
- Тестируемая часть: словарь — данные; можно добавить тест «нет непереведённых известных ключей».
- Проверка: запустить, переключить RU/EN на нескольких страницах — не должно оставаться «чужого» языка.

### Объём
Большой (сотни строк), но механический. Хорошо бить на под-задачи по разделам/вкладкам.

---

## Задача 2 — Разбивка `MainWindow.axaml` на UserControl'ы

### Симптом
`MainWindow.axaml` ~11.5k строк — тяжело поддерживать, медленная компиляция XAML, легко
потеряться (в этой сессии уже была путаница с дублирующими DEX-панелями — удалены).

### Образец (уже есть)
Вкладка Trading уже вынесена: `MainWindow.axaml` (≈ строка с `<TabItem Header="Trading">`) содержит
просто `<views:TradingDeskView />`, а вся разметка — в `Views/TradingDeskView.axaml` (+ пустой
code-behind `TradingDeskView.axaml.cs`). **Это и есть паттерн для остальных страниц.**

### Решение, которое нужно принять
Порядок выноса (какие страницы первыми) и аппетит к риску. Рекомендация — по одной странице за PR,
начиная с самых крупных/самостоятельных.

### Структура (важно)
Страницы в `MainWindow.axaml` — это `TabItem`'ы внутри `TabControl` (`SelectedIndex` биндится на
`SelectedTabIndex`), плюс «placeholder»-секция (`IsVisible="{Binding IsPlaceholderSectionVisible}"`)
для простых разделов. `DataContext` страниц — корневой `MainWindowViewModel` (биндинги вида
`{Binding DexTradingVM...}`, `{Binding SniperVM...}`). При выносе в UserControl **DataContext
наследуется** — биндинги остаются прежними (как в `TradingDeskView`). Команды/`$parent[Window]`
-биндинги проверять отдельно.

### Шаги (на каждую страницу)
1. Создать `Views/<Page>View.axaml` (UserControl) + пустой `.axaml.cs` (по образцу `TradingDeskView`).
   Объявить тот же xmlns (`ctrl:`, `vm:`, `views:`) что и в MainWindow.
2. Перенести разметку `TabItem`-содержимого (или placeholder-блока) в UserControl как есть.
3. В `MainWindow.axaml` заменить содержимое на `<views:<Page>View />`.
4. Сборка (Avalonia компилирует XAML — ошибки биндингов/неймспейсов всплывут тут) → smoke этой
   страницы (открыть, проверить что всё рендерится и кликается) → коммит.
5. Повторять по одной странице.

### Рекомендуемый порядок (от крупных/самостоятельных)
Sniper, Bots, AI Signals, Portfolio, Backtest, Markets, Dashboard, затем мелкие (Risk, Funding,
Arb, Copy, Stat Arb, News, On-Chain, Positions, Journal, Rules).

### Gotchas
- НЕ менять биндинги/логику при переносе — это чисто структурный рефакторинг (поведение не меняется).
- `x:Name`/именованные элементы, на которые ссылается code-behind MainWindow — проверить, не уедут ли.
- Привязки `{Binding $parent[Window].DataContext...}` — после выноса `$parent[Window]` всё ещё валиден
  (UserControl внутри того же Window), но перепроверить.
- Стили (`Classes="..."`) глобальны (в `App.axaml`/`AppStyles.axaml`) → работают в UserControl без правок.
- Юнит-тестов тут нет (чистый XAML) → проверка = сборка (0 ошибок) + smoke каждой вынесенной страницы.

### Объём
Большой и рискованный, но дробится на безопасные шаги (1 страница = 1 коммит/PR, легко откатить).

---

## Как начать в новой сессии
Сказать ассистенту, например: «Берём Задачу 1, язык — английский-источник» или
«Берём Задачу 2, выноси Sniper первым». Дальше — обычный цикл brainstorm → spec → plan → implement
(superpowers), спеки/планы складывать в `docs/superpowers/`.
