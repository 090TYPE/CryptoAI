namespace CryptoAITerminal.Server.Api;

/// <summary>
/// The admin panel, served as one self-contained page from the API itself.
///
/// Embedded as a string rather than a static file on purpose: it then needs no static-file
/// middleware, no csproj copy rule and no Dockerfile change, and it cannot go missing in a
/// container because it is compiled in. The page is a few kilobytes; the alternative is three
/// moving parts that can each be wrong at deploy time.
///
/// The page holds no secrets. Every read and write it performs goes to /api/admin/* or /api/keys,
/// which the licence middleware already gates behind X-Admin and which fail closed when
/// ADMIN_TOKEN is unset. The token is typed in by the operator and kept in sessionStorage, so it
/// dies with the tab rather than living in the browser profile.
///
/// It is still reachable from the internet whenever Caddy is in front, so serving it is opt-out
/// via ADMIN_UI=off for deployments that would rather reach it through an ssh tunnel only.
/// </summary>
public static class AdminUi
{
    public const string Html = """
<!doctype html>
<html lang="ru">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>CryptoAI — панель управления</title>
<style>
  :root {
    --bg:#0f1318; --card:#161b22; --card2:#1d242d; --ink:#e7ebf1; --ink2:#bfc7d3;
    --muted:#8b95a4; --line:#262f3a; --accent:#8aaad9; --good:#6fc49b; --warn:#dca954; --bad:#e08582;
    --mono: ui-monospace, "Cascadia Mono", Consolas, monospace;
  }
  *{box-sizing:border-box}
  body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 system-ui,-apple-system,sans-serif}
  header{padding:16px 20px;border-bottom:1px solid var(--line);display:flex;flex-wrap:wrap;gap:12px;align-items:center}
  header h1{margin:0;font-size:16px;font-weight:600;letter-spacing:-.01em}
  header .sp{flex:1}
  main{max-width:1100px;margin:0 auto;padding:20px}
  section{background:var(--card);border:1px solid var(--line);margin-bottom:18px}
  section>h2{margin:0;padding:13px 16px;font-size:13px;font-weight:600;letter-spacing:.04em;
    text-transform:uppercase;color:var(--muted);border-bottom:1px solid var(--line);background:var(--card2)}
  .body{padding:14px 16px}
  table{width:100%;border-collapse:collapse;font-family:var(--mono);font-size:12.5px}
  th{text-align:left;color:var(--muted);font-weight:600;font-size:11px;letter-spacing:.06em;
    text-transform:uppercase;padding:8px 10px;border-bottom:1px solid var(--line)}
  td{padding:8px 10px;border-bottom:1px solid var(--line);vertical-align:middle}
  tr:last-child td{border-bottom:none}
  .k{color:var(--accent)}
  .desc{font-family:system-ui,sans-serif;color:var(--muted);font-size:12px;margin-top:3px;max-width:52ch}
  input,select{background:var(--bg);color:var(--ink);border:1px solid var(--line);padding:6px 8px;
    font-family:var(--mono);font-size:12.5px;border-radius:2px;min-width:220px}
  input:focus,select:focus{outline:2px solid var(--accent);outline-offset:1px}
  button{background:var(--card2);color:var(--ink);border:1px solid var(--line);padding:6px 12px;
    font:inherit;font-size:12.5px;border-radius:2px;cursor:pointer}
  button:hover{border-color:var(--accent);color:var(--accent)}
  button.primary{background:var(--accent);color:#0f1318;border-color:var(--accent);font-weight:600}
  button.danger{color:var(--bad);border-color:var(--bad)}
  button.danger:hover{background:var(--bad);color:#0f1318}
  .pill{display:inline-block;font-family:var(--mono);font-size:11px;padding:2px 7px;border-radius:2px}
  .pill.on{background:rgba(111,196,155,.15);color:var(--good)}
  .pill.off{background:rgba(224,133,130,.15);color:var(--bad)}
  .pill.warn{background:rgba(220,169,84,.15);color:var(--warn)}
  .pill.n{background:var(--card2);color:var(--muted)}
  .row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
  .note{color:var(--muted);font-size:12px;margin:0 0 12px}
  .err{color:var(--bad)}
  .kill{display:flex;gap:14px;align-items:center;flex-wrap:wrap}
  .kill .state{font-size:15px;font-weight:600}
  #toast{position:fixed;right:16px;bottom:16px;background:var(--card2);border:1px solid var(--line);
    padding:10px 14px;border-radius:2px;opacity:0;transition:opacity .2s;pointer-events:none;font-size:13px}
  #toast.show{opacity:1}
  @media(max-width:640px){input,select{min-width:0;width:100%}td,th{padding:7px 8px}}
</style>
</head>
<body>
<header>
  <h1>CryptoAI — панель управления</h1>
  <span class="sp"></span>
  <span id="conn" class="pill n">не подключено</span>
  <button onclick="setToken()">Сменить токен</button>
  <button onclick="loadAll()">Обновить</button>
</header>

<main>
  <section>
    <h2>Аварийный тормоз</h2>
    <div class="body kill">
      <span class="state" id="killState">—</span>
      <button id="killBtn" onclick="toggleKill()">переключить</button>
      <span class="note" style="margin:0">Выключает все платные вызовы модели: фоновые задачи, прокси и вопросы к базе знаний. Терминалы переходят на собственные расчёты и продолжают работать.</span>
    </div>
  </section>

  <section>
    <h2>Суточные квоты</h2>
    <div class="body kill">
      <span class="state" id="budgetState">—</span>
      <button id="budgetBtn" onclick="toggleBudget()">переключить</button>
      <span class="note" style="margin:0">Предохранитель от разгона. Выключать не следует: без него держателя валидной лицензии ограничивает только рейт-лимит в 120 запросов в минуту, а это тысячи долларов в час на общий вендорский ключ.</span>
    </div>
    <div class="body kill">
      <span class="state" id="tiersState">—</span>
      <button id="tiersBtn" onclick="toggleTiers()">переключить</button>
      <span class="note" style="margin:0">Пока тарифы выключены, все лицензии делят один общий предел (<span class="k">plan.default.daily_tokens</span>) — страховка, а не прайс. Включать по измеренному расходу: числа по тарифам ниже остались с прежнего прайса и на живом использовании кончались за минуты.</span>
    </div>
    <div class="body"><table id="plans"></table></div>
  </section>

  <section>
    <h2>Модели по задачам</h2>
    <div class="body">
      <p class="note">Пусто = значение по умолчанию из кода. Изменение применяется в течение <span id="ttl">15</span> секунд, перезапуск не нужен.</p>
      <table id="models"></table>
    </div>
  </section>

  <section>
    <h2>Чем обслуживать AI</h2>
    <div class="body">
      <p class="note">Выбирается только семейство. Конкретную модель сервер подбирает сам из того, что провайдер реально отдаёт: сильную — для вызовов из терминала, дешёвую — для фоновых задач. В терминале настраивать нечего, решение приходит с сервера и действует на все выпущенные сборки сразу.</p>
      <div class="row">
        <button id="famClaude" onclick="setFamily('claude')">Claude</button>
        <button id="famGpt" onclick="setFamily('chatgpt')">ChatGPT</button>
        <span class="pill n" id="famNow">—</span>
      </div>
      <div class="row" style="margin-top:10px">
        <button id="famChoiceBtn" onclick="toggleFamilyChoice()">—</button>
        <span class="note" style="margin:0">Выбранное здесь семейство действует для тех, кто не выбрал своё в терминале. Запрет нужен, когда у одного из провайдеров перебои и увести надо всех разом.</span>
      </div>
      <table id="picked" style="margin-top:12px"></table>
      <details style="margin-top:10px">
        <summary class="desc">Что провайдер отдаёт целиком</summary>
        <div id="upstreamModels" style="margin-top:8px"></div>
      </details>
    </div>
  </section>

  <section>
    <h2>Провайдер моделей</h2>
    <div class="body">
      <p class="note">Адреса пустые = напрямую к Anthropic и OpenAI. Формат запросов у роутеров тот же, перезапуск не нужен. Ключ кладётся ниже, в «Ключи провайдеров», под именем <span class="k">anthropic</span> или <span class="k">openai</span> — на роутере это один и тот же ключ.</p>
      <p class="note">Поля с именами моделей нужны только чтобы отменить автоподбор и прибить конкретную версию. Пока они пустые, действует выбор из раздела выше.</p>
      <table id="upstream"></table>
      <div class="row" style="margin-top:12px">
        <button onclick="preset('nordrouter')">Заполнить для NordRouter</button>
        <button class="danger" onclick="preset('vendor')">Вернуть напрямую к вендорам</button>
      </div>
    </div>
  </section>

  <section>
    <h2>Модели по функциям терминала</h2>
    <div class="body">
      <p class="note">Что клиент прислал — не важно: модель выбирает сервер. Пустое поле = запрос терминала проходит как есть. Ограничение длины ответа не может превысить общий потолок сервера.</p>
      <table id="features"></table>
    </div>
  </section>

  <section>
    <h2>Пороги и объёмы фоновых задач</h2>
    <div class="body"><table id="bounds"></table></div>
  </section>

  <section>
    <h2>Все настройки в базе</h2>
    <div class="body">
      <p class="note" id="migrated"></p>
      <table id="raw"></table>
      <div class="row" style="margin-top:12px">
        <input id="newKey" placeholder="ключ" style="min-width:240px">
        <input id="newVal" placeholder="значение" style="min-width:200px">
        <button class="primary" onclick="addRaw()">Добавить</button>
      </div>
    </div>
  </section>

  <section>
    <h2>Ключи провайдеров</h2>
    <div class="body">
      <table id="keys"></table>
      <div class="row" style="margin-top:12px">
        <input id="kProv" placeholder="провайдер (anthropic, coingecko…)">
        <input id="kVal" placeholder="ключ" type="password">
        <button class="primary" onclick="saveKey()">Сохранить</button>
      </div>
    </div>
  </section>

  <section>
    <h2>Расход за 7 дней</h2>
    <div class="body">
      <p class="note">Считается по фактическим данным вендора. Цена — по тарифу Sonnet 4.6 ($3 за миллион входных, $15 за миллион выходных); для дешёвой модели фактическая сумма будет втрое ниже.</p>
      <table id="usage"></table>
    </div>
  </section>

  <section>
    <h2>Состояние сборщиков</h2>
    <div class="body"><table id="collectors"></table></div>
  </section>

  <section>
    <h2>История изменений</h2>
    <div class="body"><table id="history"></table></div>
  </section>
</main>

<div id="toast"></div>

<script>
// Known keys with human descriptions. Anything not listed still shows up in "Все настройки",
// so adding a knob on the server does not require touching this page.
const MODELS = [
  ['ai.digest.model',  'Модель для 31 общего дайджеста'],
  ['ai.score.model',   'Модель для оценки токенов'],
  ['ai.anomaly.model', 'Модель для детектора аномалий'],
  ['ai.ask.model',     'Модель для вопросов к базе знаний'],
  ['ai.review.model',  'Модель для персональных портфельных ревью'],
];
// Where requests go. A router that speaks /v1/messages is indistinguishable from Anthropic on the
// wire, so switching to one is these four fields and a key — no deploy.
const UPSTREAM = [
  ['ai.anthropic.base_url',      'Адрес для запросов формата Anthropic. Пусто = api.anthropic.com'],
  ['ai.openai.base_url',         'Адрес для запросов формата OpenAI. Пусто = api.openai.com'],
  ['ai.allowed_models.anthropic','Разрешённые модели через запятую. У роутеров имена с префиксом: anthropic/claude-sonnet-4.6'],
  ['ai.allowed_models.openai',   'То же для формата OpenAI. Пусто = список из переменных окружения'],
  ['ai.anthropic.model.default', 'Чем подменять модель, если у вызова нет своей. Обязательно при переходе на роутер: иначе терминалы шлют вендорское имя и получают «model is not allowed»'],
  ['ai.openai.model.default',    'То же для формата OpenAI'],
];
// Действуют только когда включён ai.budget.enforced. Пустое поле = число из кода, которое там
// стоит как форма, а не как решение: пересчитывать его надо по ai_usage.
const PLANS = [
  ['plan.lite.daily_tokens',    'Суточный предел тарифа Lite. Пусто = 35 000 из кода'],
  ['plan.pro.daily_tokens',     'Суточный предел тарифа Pro. Пусто = 70 000 из кода'],
  ['plan.max.daily_tokens',     'Суточный предел тарифа Max. Пусто = 105 000 из кода'],
  ['plan.default.daily_tokens', 'Общий предел, пока тарифы не применяются, и запасной для незнакомого тарифа. Пусто = 2 000 000 токенов в сутки'],
];
const BOUNDS = [
  ['ai.score.batch',            'Сколько токенов оценивать за один проход'],
  ['ai.score.max_age_hours',    'Через сколько часов оценка считается устаревшей'],
  ['ai.anomaly.min_move_pct',   'Порог движения за час, в процентах — главный ограничитель расхода'],
  ['ai.anomaly.cooldown_hours', 'Пауза по одному токену между срабатываниями'],
  ['ai.anomaly.batch',          'Сколько кандидатов проверять за один проход'],
  ['ai.review.days',            'Как часто присылать портфельное ревью, в днях'],
  ['ai.review.batch',           'Сколько пользователей обрабатывать за час'],
];

let token = sessionStorage.getItem('adminToken') || '';
let current = {};

function setToken() {
  const t = prompt('Админский токен (ADMIN_TOKEN):', token);
  if (t === null) return;
  token = t.trim();
  sessionStorage.setItem('adminToken', token);
  loadAll();
}

function toast(msg, bad) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.style.color = bad ? 'var(--bad)' : 'var(--ink)';
  el.classList.add('show');
  setTimeout(() => el.classList.remove('show'), 2600);
}

async function api(path, opts = {}) {
  const res = await fetch(path, {
    ...opts,
    headers: { 'X-Admin': token, 'Content-Type': 'application/json', ...(opts.headers || {}) }
  });
  if (res.status === 401 || res.status === 403) throw new Error('токен отклонён');
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.status === 204 ? null : res.json();
}

const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const val = k => current[k]?.value ?? '';

function editableRows(defs) {
  return defs.map(([k, d]) => `
    <tr>
      <td style="width:42%"><span class="k">${esc(k)}</span><div class="desc">${esc(d)}</div></td>
      <td><input id="in_${esc(k)}" value="${esc(val(k))}" placeholder="по умолчанию"></td>
      <td style="width:1%;white-space:nowrap">
        <button onclick="save('${esc(k)}')">Сохранить</button>
        ${current[k] ? `<button class="danger" onclick="reset('${esc(k)}')">Сбросить</button>` : ''}
      </td>
    </tr>`).join('');
}

async function loadAll() {
  if (!token) { setToken(); return; }
  const conn = document.getElementById('conn');
  try {
    const s = await api('/api/admin/settings');
    conn.className = 'pill on'; conn.textContent = 'подключено';

    current = {};
    (s.settings || []).forEach(x => current[x.key] = x);
    document.getElementById('ttl').textContent = s.ttlSeconds ?? 15;
    document.getElementById('migrated').innerHTML =
      !s.migrated
        ? '<span class="err">Миграция 021 не применена — сервер работает на значениях по умолчанию, сохранение не сработает. См. раздел 5b в docs/DEPLOY.md.</span>'
        : s.degraded
          ? '<span class="err">База недоступна. Сервер отдаёт последний известный снимок настроек — изменения сейчас не сохранятся и не применятся.</span>'
          : `Ключей в базе: ${(s.settings || []).length}. Отсутствующий ключ означает значение по умолчанию из кода.`;

    document.getElementById('upstream').innerHTML = editableRows(UPSTREAM);
    document.getElementById('models').innerHTML = editableRows(MODELS);
    document.getElementById('bounds').innerHTML = editableRows(BOUNDS);
    document.getElementById('plans').innerHTML = editableRows(PLANS);

    const known = new Set([...MODELS, ...BOUNDS, ...UPSTREAM, ...PLANS].map(x => x[0])
      .concat(['ai.enabled', 'ai.anthropic.auth_bearer', 'ai.family', 'ai.model.auto',
               'ai.family.user_choice', 'ai.budget.enforced', 'plan.tiers.enforced']));
    const rest = (s.settings || []).filter(x => !known.has(x.key));
    document.getElementById('raw').innerHTML = rest.length
      ? '<tr><th>Ключ</th><th>Значение</th><th>Изменено</th><th></th></tr>' + rest.map(x => `
          <tr><td class="k">${esc(x.key)}</td><td>${esc(x.value)}</td>
          <td class="pill n">${esc((x.updatedUtc || '').replace('T', ' ').slice(0, 16))}</td>
          <td><button class="danger" onclick="reset('${esc(x.key)}')">Удалить</button></td></tr>`).join('')
      : '<tr><td class="desc">Других ключей нет.</td></tr>';

    const killOn = (current['ai.enabled']?.value ?? 'true').toLowerCase() !== 'false';
    document.getElementById('killState').innerHTML = killOn
      ? '<span class="pill on">AI включён</span>'
      : '<span class="pill off">AI остановлен</span>';
    document.getElementById('killBtn').className = killOn ? 'danger' : 'primary';
    document.getElementById('killBtn').textContent = killOn ? 'Остановить все вызовы' : 'Включить обратно';

    // Предел действует по умолчанию: см. SettingKeys.DefaultAiBudgetEnforced.
    const budgetOn = (current['ai.budget.enforced']?.value ?? 'true').toLowerCase() !== 'false';
    document.getElementById('budgetState').innerHTML = budgetOn
      ? '<span class="pill on">предел действует</span>'
      : '<span class="pill off">ПРЕДЕЛА НЕТ</span>';
    document.getElementById('budgetBtn').className = budgetOn ? 'danger' : 'primary';
    document.getElementById('budgetBtn').textContent = budgetOn ? 'Снять предел совсем' : 'Вернуть предел';

    // Тарифы по умолчанию не применяются: идёт тестовый период.
    const tiersOn = (current['plan.tiers.enforced']?.value ?? 'false').toLowerCase() === 'true';
    document.getElementById('tiersState').innerHTML = tiersOn
      ? '<span class="pill on">тарифы применяются</span>'
      : '<span class="pill n">тестовый период — один предел на всех</span>';
    document.getElementById('tiersBtn').className = tiersOn ? 'danger' : 'primary';
    document.getElementById('tiersBtn').textContent = tiersOn ? 'Вернуть общий предел' : 'Включить тарифы';

    await Promise.all([loadUpstream(), loadFeatures(), loadUsage(), loadKeys(), loadCollectors(), loadHistory()]);
  } catch (e) {
    conn.className = 'pill off'; conn.textContent = 'ошибка';
    toast(e.message, true);
  }
}

async function save(k) {
  const v = document.getElementById('in_' + k).value.trim();
  try {
    if (!v) { await reset(k, true); return; }
    await api('/api/admin/settings/' + encodeURIComponent(k), { method: 'PUT', body: JSON.stringify({ value: v }) });
    toast(k + ' сохранено');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function reset(k, quiet) {
  try {
    await api('/api/admin/settings/' + encodeURIComponent(k), { method: 'DELETE' });
    if (!quiet) toast(k + ' сброшено к значению по умолчанию');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function addRaw() {
  const k = document.getElementById('newKey').value.trim();
  const v = document.getElementById('newVal').value.trim();
  if (!k || !v) { toast('нужны и ключ, и значение', true); return; }
  try {
    await api('/api/admin/settings/' + encodeURIComponent(k), { method: 'PUT', body: JSON.stringify({ value: v }) });
    document.getElementById('newKey').value = ''; document.getElementById('newVal').value = '';
    toast('добавлено'); loadAll();
  } catch (e) { toast(e.message, true); }
}

async function toggleKill() {
  const on = (current['ai.enabled']?.value ?? 'true').toLowerCase() !== 'false';
  if (on && !confirm('Остановить все платные вызовы модели?\n\nДайджесты, оценки, аномалии и запросы из терминалов перестанут выполняться. Терминалы перейдут на собственные расчёты.')) return;
  try {
    await api('/api/admin/settings/ai.enabled', { method: 'PUT', body: JSON.stringify({ value: on ? 'false' : 'true' }) });
    toast(on ? 'AI остановлен' : 'AI включён');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function toggleBudget() {
  const on = (current['ai.budget.enforced']?.value ?? 'true').toLowerCase() !== 'false';
  // Подтверждение стоит на ВЫКЛЮЧЕНИИ: включённый предел — норма, а снятый оставляет платный ключ
  // сервера без единого потолка в токенах.
  if (on && !confirm('Снять суточный предел совсем?\n\nПосле этого расход одной лицензии ограничивает только рейт-лимит — 120 запросов в минуту, то есть тысячи долларов в час на общий вендорский ключ. Если цель в том, чтобы тарифы не мешали тестировать, выключайте ТАРИФЫ, а не предел.')) return;
  try {
    await putOrDelete('ai.budget.enforced', on ? 'false' : 'true');
    toast(on ? 'Предел снят — сервер без потолка трат' : 'Предел возвращён');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function toggleTiers() {
  const on = (current['plan.tiers.enforced']?.value ?? 'false').toLowerCase() === 'true';
  if (!on && !confirm('Включить пределы по тарифам?\n\nПроверьте числа в полях ниже: они остались с прежнего прайса и на живом использовании кончались за минуты. Клиент, упёршийся в свой тариф, получит 429 и переключится на встроенные расчёты.')) return;
  try {
    await putOrDelete('plan.tiers.enforced', on ? 'false' : 'true');
    toast(on ? 'Тарифы выключены — один предел на всех' : 'Тарифы применяются');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

// Rendered from the server's own catalogue, so a feature added there appears here without an edit.
// Both fields must move together: a router names models with a vendor prefix, so changing only the
// URL leaves every call rejected as "model is not allowed" — which reads like a client bug.
async function preset(which) {
  // Имена моделей пресет не пишет: их подбирает сервер по живому списку провайдера. Раньше он их
  // писал — и именно угаданные имена были причиной того, что переключение выглядело сделанным, а
  // каждый вызов отвечал «model is not allowed». Старые значения чистятся по той же причине.
  const values = which === 'nordrouter'
    ? {
        'ai.anthropic.base_url': 'https://nordrouter.com/v1/messages',
        'ai.openai.base_url': 'https://nordrouter.com/v1/chat/completions',
        'ai.allowed_models.anthropic': '', 'ai.allowed_models.openai': ''
      }
    : { 'ai.anthropic.base_url': '', 'ai.openai.base_url': '', 'ai.allowed_models.anthropic': '', 'ai.allowed_models.openai': '' };

  const label = which === 'nordrouter' ? 'перевести все AI-вызовы на NordRouter' : 'вернуть вызовы напрямую к вендорам';
  if (!confirm('Точно ' + label + '?\n\nКлюч в разделе «Ключи провайдеров» должен соответствовать выбранному адресу, иначе все вызовы начнут отвечать 401.')) return;

  try {
    for (const [k, v] of Object.entries(values)) await putOrDelete(k, v);
    toast(which === 'nordrouter' ? 'Переключено на NordRouter' : 'Возвращено к вендорам');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function setFamily(f) {
  const name = f === 'chatgpt' ? 'ChatGPT' : 'Claude';
  if (!confirm('Перевести все AI-вызовы на ' + name + '?\n\nПодействует на терминалы и на фоновые задачи в течение ' +
    (document.getElementById('ttl').textContent || 15) + ' секунд.')) return;
  try {
    await putOrDelete('ai.family', f);
    toast('Семейство: ' + name);
    loadAll();
  } catch (e) { toast(e.message, true); }
}

async function toggleFamilyChoice() {
  const allowed = (current['ai.family.user_choice']?.value ?? 'true').toLowerCase() !== 'false';
  if (!confirm(allowed
    ? 'Запретить пользователям выбирать семейство?\n\nВсе терминалы перейдут на выбранное здесь.'
    : 'Вернуть пользователям выбор семейства?')) return;
  try {
    await putOrDelete('ai.family.user_choice', allowed ? 'false' : 'true');
    toast(allowed ? 'Выбор в терминале запрещён' : 'Выбор в терминале разрешён');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

// Показывает не то, что настроено, а то, что сервер выберет прямо сейчас. Разница существенная:
// настройка может быть сделана правильно, а провайдер при этом не отдавать ни одной подходящей
// модели — и увидеть это надо здесь, а не по молчащим дайджестам через сутки.
//
// Спрашиваются ОБА семейства, а не только выбранное здесь. Пока выбор разрешён в терминале,
// половина пользователей может сидеть на другом семействе, и его неработающий ключ не был бы виден
// в админке ничем: раздел показывал бы «всё хорошо» ровно про то семейство, которым эти люди не
// пользуются.
async function loadUpstream() {
  const picked = document.getElementById('picked');
  const all = document.getElementById('upstreamModels');
  try {
    const [claude, gptFam] = await Promise.all([
      api('/api/admin/upstream-models?family=claude'),
      api('/api/admin/upstream-models?family=chatgpt'),
    ]);

    const serverFamily = (current['ai.family']?.value ?? 'claude').toLowerCase();
    const gpt = serverFamily === 'chatgpt' || serverFamily === 'openai' || serverFamily === 'gpt';
    document.getElementById('famNow').textContent = gpt ? 'сейчас ChatGPT' : 'сейчас Claude';
    document.getElementById('famClaude').className = gpt ? '' : 'primary';
    document.getElementById('famGpt').className = gpt ? 'primary' : '';

    const choice = (current['ai.family.user_choice']?.value ?? 'true').toLowerCase() !== 'false';
    const btn = document.getElementById('famChoiceBtn');
    btn.textContent = choice ? 'Запретить выбор в терминале' : 'Разрешить выбор в терминале';
    btn.className = choice ? '' : 'danger';

    const auto = claude.auto !== false;
    const row = (label, r, isDefault) => {
      const tag = isDefault ? ' <span class="pill on">по умолчанию</span>' : '';
      if (!r.ok)
        return '<tr><td>' + label + tag + '</td><td colspan="2" class="err">' + esc(r.error || 'нет ответа') +
               ' — запрашивали ' + esc(r.url || '') + '. Проверьте ключ и адрес.</td></tr>';
      if (!r.foreground || !r.background)
        return '<tr><td>' + label + tag + '</td><td colspan="2" class="err">Подходящей модели нет в списке провайдера' +
               ' — вызовы пойдут на значения из полей ниже.</td></tr>';
      return '<tr><td>' + label + tag + '</td><td class="k">' + esc(r.foreground) + '</td><td class="k">' + esc(r.background) + '</td></tr>';
    };

    picked.innerHTML = !auto
      ? '<tr><td class="desc">Автоподбор выключен (ai.model.auto). Действуют имена, заданные вручную ниже.</td></tr>'
      : '<tr><th>Семейство</th><th>Вызовы из терминала</th><th>Фоновые задачи</th></tr>' +
        row('Claude', claude, !gpt) + row('ChatGPT', gptFam, gpt) +
        (choice ? '<tr><td colspan="3" class="desc">Выбор разрешён в терминале, поэтому рабочими должны быть обе строки: пользователь может сидеть на любой из них.</td></tr>' : '');

    const listing = (label, r) =>
      '<div style="margin-bottom:10px"><div class="desc">' + label + '</div>' +
      ((r.models || []).length
        ? (r.models || []).map(m => '<span class="k" style="display:inline-block;margin:0 10px 6px 0">' + esc(m) + '</span>').join('')
        : '<span class="desc">Провайдер не вернул ни одной модели.</span>') + '</div>';
    all.innerHTML = listing('Claude', claude) + listing('ChatGPT', gptFam);
  } catch (e) {
    picked.innerHTML = '<tr><td class="err">' + esc(e.message) + '</td></tr>';
  }
}

async function loadFeatures() {
  const el = document.getElementById('features');
  try {
    const rows = await api('/api/admin/ai-features');
    el.innerHTML =
      '<tr><th>Функция</th><th>Модель</th><th>Длина ответа</th><th></th></tr>' +
      rows.map(f => `<tr>
        <td style="width:38%">${esc(f.title)}<div class="desc"><span class="k">${esc(f.id)}</span> · клиент просит ${f.defaultMaxTokens}</div></td>
        <td><input id="fm_${esc(f.id)}" value="${esc(f.model || '')}" placeholder="как просит клиент"></td>
        <td><input id="ft_${esc(f.id)}" value="${f.maxTokens ? f.maxTokens : ''}" placeholder="${f.defaultMaxTokens}" style="min-width:110px"></td>
        <td style="width:1%;white-space:nowrap"><button onclick="saveFeature('${esc(f.id)}')">Сохранить</button></td>
      </tr>`).join('') +
      `<tr><td colspan="4" style="padding-top:12px">
         <button onclick="bulkFeatures('claude-haiku-4-5-20251001')">Все на экономичную модель</button>
         <button onclick="bulkFeatures('')" class="danger">Снять все переопределения</button>
       </td></tr>`;
  } catch (e) { el.innerHTML = `<tr><td class="err">${esc(e.message)}</td></tr>`; }
}

async function saveFeature(id) {
  const m = document.getElementById('fm_' + id).value.trim();
  const t = document.getElementById('ft_' + id).value.trim();
  try {
    await putOrDelete('ai.feature.' + id + '.model', m);
    await putOrDelete('ai.feature.' + id + '.max_tokens', t);
    toast(id + ' сохранено');
    loadAll();
  } catch (e) { toast(e.message, true); }
}

// Empty value means "no override", which is a delete rather than an empty string - an empty model
// name would fail the allow-list check on every call instead of falling through to the client's.
async function putOrDelete(key, value) {
  const path = '/api/admin/settings/' + encodeURIComponent(key);
  if (value) return api(path, { method: 'PUT', body: JSON.stringify({ value }) });
  try { await api(path, { method: 'DELETE' }); } catch (e) { if (!/404|HTTP 404/.test(e.message)) throw e; }
}

async function bulkFeatures(model) {
  const what = model ? `перевести ВСЕ функции на ${model}` : 'снять переопределения со ВСЕХ функций';
  if (!confirm('Точно ' + what + '?')) return;
  try {
    const rows = await api('/api/admin/ai-features');
    for (const f of rows) await putOrDelete('ai.feature.' + f.id + '.model', model);
    toast('готово'); loadAll();
  } catch (e) { toast(e.message, true); }
}

async function loadUsage() {
  const el = document.getElementById('usage');
  try {
    const rows = await api('/api/admin/usage?days=7');
    if (!rows.length) { el.innerHTML = '<tr><td class="desc">Данных пока нет — учёт начинается с первого вызова после применения миграции 023.</td></tr>'; return; }
    // Output is priced five times input on every model here, so a total-token column would rank
    // the list wrong. Sort and show money instead.
    const cost = r => (r.inputTokens * 3 + r.outputTokens * 15) / 1e6;
    const total = rows.reduce((s, r) => s + cost(r), 0);
    el.innerHTML =
      '<tr><th>Функция</th><th>Модель</th><th class="num">Вызовов</th><th class="num">Вход</th><th class="num">Выход</th><th class="num">≈ $</th></tr>' +
      rows.map(r => `<tr>
        <td class="k">${esc(r.feature || 'серверные задачи')}</td>
        <td class="desc" style="font-family:var(--mono)">${esc(r.model || '')}</td>
        <td style="text-align:right">${r.calls}</td>
        <td style="text-align:right">${(r.inputTokens / 1000).toFixed(1)}k</td>
        <td style="text-align:right">${(r.outputTokens / 1000).toFixed(1)}k</td>
        <td style="text-align:right">${cost(r).toFixed(2)}</td></tr>`).join('') +
      `<tr><td colspan="5" style="text-align:right;font-weight:600">Итого за 7 дней</td>
           <td style="text-align:right;font-weight:600">${total.toFixed(2)}</td></tr>`;
  } catch (e) { el.innerHTML = `<tr><td class="err">${esc(e.message)}</td></tr>`; }
}

async function loadKeys() {
  try {
    const list = await api('/api/keys');
    document.getElementById('keys').innerHTML =
      '<tr><th>Провайдер</th><th>Ключ</th><th>Состояние</th><th>Изменён</th></tr>' +
      (list || []).map(k => `<tr>
        <td class="k">${esc(k.provider)}</td>
        <td>${k.hasKey ? esc(k.masked) : '<span class="desc">не задан</span>'}</td>
        <td>${k.enabled ? '<span class="pill on">включён</span>' : '<span class="pill off">выключен</span>'}</td>
        <td class="pill n">${esc((k.updatedUtc || '').replace('T', ' ').slice(0, 16))}</td></tr>`).join('');
  } catch (e) { document.getElementById('keys').innerHTML = `<tr><td class="err">${esc(e.message)}</td></tr>`; }
}

async function saveKey() {
  const p = document.getElementById('kProv').value.trim();
  const v = document.getElementById('kVal').value.trim();
  if (!p || !v) { toast('нужны провайдер и ключ', true); return; }
  try {
    await api('/api/keys/' + encodeURIComponent(p), { method: 'PUT', body: JSON.stringify({ apiKey: v, enabled: true, note: 'panel' }) });
    document.getElementById('kVal').value = '';
    toast('ключ сохранён'); loadKeys();
  } catch (e) { toast(e.message, true); }
}

// Rendered generically from whatever the endpoint returns, so a new field on the server shows up
// here without an edit.
async function loadCollectors() {
  const el = document.getElementById('collectors');
  try {
    const data = await api('/api/admin/collectors');
    const rows = Array.isArray(data) ? data : (data.collectors || data.items || []);
    if (!rows.length) { el.innerHTML = '<tr><td class="desc">Пока нет данных о запусках.</td></tr>'; return; }
    const cols = [...new Set(rows.flatMap(r => Object.keys(r)))];
    el.innerHTML = '<tr>' + cols.map(c => `<th>${esc(c)}</th>`).join('') + '</tr>' +
      rows.map(r => '<tr>' + cols.map(c => {
        const v = r[c];
        if (typeof v === 'boolean') return `<td><span class="pill ${v ? 'on' : 'off'}">${v ? 'да' : 'нет'}</span></td>`;
        return `<td>${esc(typeof v === 'string' ? v.replace('T', ' ').slice(0, 19) : v)}</td>`;
      }).join('') + '</tr>').join('');
  } catch (e) { el.innerHTML = `<tr><td class="err">${esc(e.message)}</td></tr>`; }
}

async function loadHistory() {
  const el = document.getElementById('history');
  try {
    const rows = await api('/api/admin/settings/history?limit=40');
    el.innerHTML = (rows || []).length
      ? '<tr><th>Когда</th><th>Ключ</th><th>Было</th><th>Стало</th><th>Кто</th></tr>' + rows.map(h => `<tr>
          <td class="pill n">${esc((h.changedUtc || '').replace('T', ' ').slice(0, 16))}</td>
          <td class="k">${esc(h.key)}</td>
          <td>${h.oldValue === null ? '<span class="desc">—</span>' : esc(h.oldValue)}</td>
          <td>${h.newValue === null ? '<span class="pill off">удалено</span>' : esc(h.newValue)}</td>
          <td class="desc">${esc(h.changedBy || '')}</td></tr>`).join('')
      : '<tr><td class="desc">Изменений пока не было.</td></tr>';
  } catch (e) { el.innerHTML = `<tr><td class="err">${esc(e.message)}</td></tr>`; }
}

loadAll();
</script>
</body>
</html>
""";
}
