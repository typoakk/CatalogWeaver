'use strict';
const $ = (id) => document.getElementById(id);
const h = (value) =>
  String(value ?? '').replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]
  );
const n = (value, digits = 0) =>
  value == null ? '—' : Number(value).toLocaleString('ru-RU', { maximumFractionDigits: digits });
const bytes = (value) => {
  if (value == null) return '—';
  const units = ['Б', 'КиБ', 'МиБ', 'ГиБ', 'ТиБ'];
  let v = Number(value),
    i = 0;
  while (v >= 1024 && i < 4) {
    v /= 1024;
    i++;
  }
  return n(v, i ? 1 : 0) + ' ' + units[i];
};
const date = (value) =>
  value
    ? new Date(value).toLocaleString('ru-RU', {
        day: '2-digit',
        month: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit',
      })
    : '—';
const age = (value) => {
  if (value == null) return '—';
  const s = Math.max(0, Math.floor(Number(value)));
  return s < 60
    ? s + ' с'
    : s < 3600
      ? Math.floor(s / 60) + ' мин'
      : s < 86400
        ? n(s / 3600, 1) + ' ч'
        : n(s / 86400, 1) + ' д';
};
const labels = {
  pending: 'Ожидает',
  importing: 'Выполняется',
  completed: 'Успешно',
  partial: 'Частично',
  retry: 'Повтор',
  failed: 'Ошибка',
  cancelled: 'Отменено',
  unavailable: 'Недоступно',
  skipped: 'Пропущено',
  discovery: 'Обход источника',
  metadata: 'Метаданные',
  images: 'Изображения',
  discovering: 'Обход',
  idle: 'Ожидание',
  running: 'Работает',
  starting: 'Запуск',
  degraded: 'Сбой',
  stopped: 'Остановлен',
  recovery: 'Восстановление',
  finalizing: 'Завершение',
  complete: 'Готово',
  queued: 'В очереди',
  stored: 'На диске',
  remote: 'Ссылка',
  missing: 'Отсутствует',
  database: 'Целевая БД',
  publishing: 'Публикация',
  acquire_lock: 'Ожидание блокировки',
};
const label = (value) => labels[value] || value || '—';
const sum = (items, field) => (items || []).reduce((a, x) => a + Number(x[field] || 0), 0);
const empty = (title, text = '') =>
  `<div class="empty"><strong>${h(title)}</strong>${h(text)}</div>`;
const kv = (rows) =>
  `<dl class="kv">${rows.map(([k, v]) => `<dt>${h(k)}</dt><dd>${h(v ?? '—')}</dd>`).join('')}</dl>`;
const table = (headers, rows) =>
  `<div class="table-wrap"><table><thead><tr>${headers.map((x) => `<th>${h(x)}</th>`).join('')}</tr></thead><tbody>${rows.join('')}</tbody></table></div>`;
const cell = (value) => `<td>${h(value ?? '—')}</td>`;
const state = {
  environments: [],
  env: 'all',
  provider: null,
  tab: 'overview',
  hours: 1,
  history: [],
  jobs: [],
  jobState: '',
  jobCursor: null,
  cursorStack: [],
  nextCursor: null,
  generation: 0,
  loading: false,
};
let timer,
  historyKey = '',
  jobsKey = '',
  toastTimer;
function toast(message) {
  $('toast').textContent = message;
  $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => ($('toast').hidden = true), 6000);
}
async function api(path, options = {}) {
  const response = await fetch('/monitor/api' + path, {
    cache: 'no-store',
    ...options,
    headers: {
      'Content-Type': 'application/json',
      'X-CatalogWeaver-Request': 'monitor',
      ...options.headers,
    },
  });
  if (!response.ok) {
    let data = {};
    try {
      data = await response.json();
    } catch {}
    const messages = {
      server_already_connected: 'Этот сервер уже подключён.',
      server_identity_changed: 'По этому адресу другой сервер. Добавьте его отдельным окружением.',
      connection_failed: 'Не удалось подключиться. Проверьте адрес и ключ.',
      invalid_monitor_key: 'Нужен ключ мониторинга сервера.',
      monitor_key_rejected:
        'Сервер отклонил ключ. Используй его ключ мониторинга, а не ключ управления.',
      monitor_unreachable:
        'С этого ПК не удалось подключиться к серверу. Проверь адрес или SSH-туннель.',
      monitor_timeout: 'Сервер не ответил за отведённое время.',
      monitor_incompatible_schema: 'Версия сервера не поддерживает этот формат мониторинга.',
      monitor_server_identity_changed:
        'По этому адресу другой сервер. Добавь его отдельным окружением.',
    };
    throw new Error(
      messages[data.error] || data.message || `Запрос не выполнен (HTTP ${response.status}).`
    );
  }
  return response.status === 204 ? null : response.json();
}
function badge(text, kind = '') {
  return `<span class="badge ${kind}"><i class="dot ${kind}"></i>${h(text)}</span>`;
}
function warnings(env) {
  const data = env.data;
  if (!data || !env.enabled) return [];
  const result = [];
  if (
    data.resources?.diskFreeBytes != null &&
    data.resources.diskFreeBytes < (env.minimumFreeBytes ?? 5368709120)
  )
    result.push('Свободное место ниже порога: ' + bytes(data.resources.diskFreeBytes));
  if (data.workersEnabled)
    for (const a of data.adapters || []) {
      if (!a.enabled) continue;
      const gate = a.limiter?.[0];
      if (gate?.blockedUntilUtc && Date.parse(gate.blockedUntilUtc) > Date.now()) continue;
      const waiting = a.waiting?.[0],
        active = (data.loops || []).filter(
          (x) => x.provider === a.provider && x.state === 'running'
        );
      const since = Math.max(
        Date.parse(data.sinceUtc) || 0,
        Date.parse(waiting?.eligibleSinceUtc) || 0,
        Date.parse(a.items?.[0]?.lastSavedUtc) || 0,
        ...active.map((x) => Date.parse(x.stageStartedUtc) || Date.now())
      );
      if (
        ((waiting?.dueNow || 0) > 0 || active.length > 0) &&
        Date.now() - since > (env.noProgressMinutes ?? 15) * 60000
      )
        result.push(a.name + ': нет прогресса более ' + (env.noProgressMinutes ?? 15) + ' мин');
    }
  return result;
}
function health(env) {
  if (!env.enabled) return ['Опрос отключён', 'off'];
  if (env.error) return ['Нет связи', 'bad'];
  if (!env.data) return ['Подключение', 'off'];
  if (
    Date.now() - Date.parse(env.data.updatedUtc) >
    Math.max(45000, (env.intervalSeconds * 2 + 10) * 1000)
  )
    return ['Данные устарели', 'warn'];
  if (env.data.collectorError || !env.data.healthy) return ['Требует внимания', 'bad'];
  if (warnings(env).length) return ['Требует внимания', 'warn'];
  if (env.data.adapters?.some((x) => x.errors?.length || x.states?.failed))
    return ['Есть проблемы', 'warn'];
  return ['На связи', ''];
}
function current() {
  return state.environments.find((x) => String(x.id) === state.env);
}
function selectedAdapters() {
  const data = current()?.data;
  return (data?.adapters || []).filter((x) => !state.provider || x.provider === state.provider);
}
function aggregate(adapters) {
  const result = {
    states: {},
    rates: [1, 5, 15].map((minutes) => ({
      minutes,
      importsPerMinute: 0,
      refreshesPerMinute: 0,
      fullWindow: true,
      observedSeconds: minutes * 60,
    })),
  };
  for (const a of adapters) {
    for (const [k, v] of Object.entries(a.states || {}))
      result.states[k] = (result.states[k] || 0) + v;
    for (const r of a.rates || []) {
      const target = result.rates.find((x) => x.minutes === r.minutes);
      if (target) {
        target.importsPerMinute += r.importsPerMinute;
        target.refreshesPerMinute += r.refreshesPerMinute;
        target.fullWindow &&= r.fullWindow;
        target.observedSeconds = Math.min(target.observedSeconds, r.observedSeconds);
      }
    }
  }
  return result;
}
function ratesMarkup(rates, field = 'importsPerMinute') {
  return `<div class="rates">${[1, 5, 15]
    .map((m) => {
      const r = rates?.find((x) => x.minutes === m);
      return `<div class="rate"><label>M${m} · ${m} мин</label><strong>${n(r?.[field], 1)}</strong><small>${r ? (r.fullWindow ? 'Полное окно' : 'Измерено ' + age(r.observedSeconds)) : 'Нет измерений'}</small></div>`;
    })
    .join('')}</div>`;
}
function card(title, value, note, accent = false) {
  return `<div class="card"><div class="label">${h(title)}</div><div class="number ${accent ? 'accent' : ''}">${h(value)}</div><div class="fine">${h(note)}</div></div>`;
}
function navigate(env, provider = null, tab = 'overview') {
  state.env = String(env);
  state.provider = provider;
  state.tab = tab;
  state.generation++;
  state.history = [];
  state.jobs = [];
  state.jobCursor = null;
  state.cursorStack = [];
  state.nextCursor = null;
  historyKey = '';
  jobsKey = '';
  location.hash = new URLSearchParams({
    env: state.env,
    ...(provider ? { adapter: provider } : {}),
    tab,
  }).toString();
  render();
  loadSecondary().catch((error) => toast(error.message));
}
function renderNavigation() {
  $('environment-count').textContent = state.environments.length;
  $('all-environments').classList.toggle('selected', state.env === 'all');
  $('environment-list').innerHTML = state.environments
    .map(
      (env) =>
        `<button class="nav-item ${state.env === String(env.id) ? 'selected' : ''}" data-env="${h(env.id)}"><i class="dot ${health(env)[1]}"></i><span>${h(env.name)}</span></button>`
    )
    .join('');
  const adapters = current()?.data?.adapters || [];
  $('adapter-count').textContent = adapters.length || '';
  $('adapter-list').innerHTML = adapters.length
    ? `<button class="nav-item ${!state.provider ? 'selected' : ''}" data-provider=""><span>◈</span><span>Все адаптеры</span></button>` +
      adapters
        .map(
          (a) =>
            `<button class="nav-item ${state.provider === a.provider ? 'selected' : ''}" data-provider="${h(a.provider)}"><i class="dot ${a.errors?.length ? 'bad' : a.enabled ? '' : 'off'}"></i><span>${h(a.name)}</span><span class="count">${n((a.states?.pending || 0) + (a.states?.retry || 0) + (a.states?.partial || 0))}</span></button>`
        )
        .join('')
    : '';
}
function overviewAll() {
  const active = state.environments.filter(
    (x) =>
      x.enabled &&
      x.data &&
      !x.error &&
      Date.now() - Date.parse(x.data.updatedUtc) <=
        Math.max(45000, (x.intervalSeconds * 2 + 10) * 1000)
  ).length;
  $('breadcrumbs').textContent = 'Мониторинг / Все окружения';
  return `<div class="page-heading"><div><p class="eyebrow">ОБЩАЯ КАРТИНА</p><h1>Все окружения</h1><p class="subtitle">${n(active)} на связи · ${n(state.environments.length)} подключено к этому ПК</p></div><button class="primary" data-action="add">+ Добавить окружение</button></div>${!state.environments.length ? '<section class="panel"><div class="empty"><strong>Подключи первый сервер</strong>Добавь адрес CatalogWeaver и его ключ мониторинга. Этот клиент будет получать показатели каждого окружения отдельно.</div></section>' : ''}<div class="environment-grid">${state.environments
    .map((env) => {
      const data = env.data,
        summary = aggregate(data?.adapters || []),
        s = summary.states;
      return `<section class="environment-card" style="border-top-color:${h(env.color)}"><div class="panel-header"><div><h2>${h(env.name)}</h2><span class="fine">${h(env.description || env.address || 'Этот сервер')}</span></div>${badge(...health(env))}</div>${ratesMarkup(data ? summary.rates : null)}<p class="fine gap-top">Новые карточки / минуту</p>${kv(
        [
          ['Адаптеры', n(data?.adapters?.length)],
          ['В работе', data ? n(s.importing || 0) : '—'],
          [
            'Ожидают / повтор',
            data ? n((s.pending || 0) + (s.retry || 0) + (s.partial || 0)) : '—',
          ],
          ['Ошибки заданий', data ? n(s.failed || 0) : '—'],
          ['Снимок', date(data?.updatedUtc)],
        ]
      )}<button class="small-button" data-env="${h(env.id)}">Открыть окружение →</button></section>`;
    })
    .join('')}</div>`;
}
function chartMarkup() {
  const snapshots = state.history,
    points = snapshots.map((snapshot) => ({
      time: Date.parse(snapshot.updatedUtc),
      rates: aggregate(
        (snapshot.adapters || []).filter((x) => !state.provider || x.provider === state.provider)
      ).rates,
    }));
  if (!points.length)
    return empty(
      'История накапливается',
      'Первые измерения появятся после начала работы сборщика.'
    );
  const end = Date.now(),
    start = end - state.hours * 3600000;
  const max = Math.max(1, ...points.flatMap((p) => p.rates.map((x) => x.importsPerMinute)));
  const colors = ['#72cfb4', '#88a9e9', '#d2b180'];
  return `<svg class="chart" viewBox="0 0 800 140" preserveAspectRatio="none" role="img" aria-label="Скорость первых успешных импортов за выбранный период"><path class="chart-grid" d="M0 20H800M0 70H800M0 120H800"/>${[
    1, 5, 15,
  ]
    .map((m, i) => {
      const segments = [];
      let segment = [];
      for (let j = 0; j < points.length; j++) {
        const p = points[j];
        if (
          j &&
          p.time - points[j - 1].time > Math.max(180000, ((state.hours * 3600000) / 500) * 2)
        ) {
          if (segment.length) segments.push(segment);
          segment = [];
        }
        segment.push(
          `${Math.max(0, Math.min(800, ((p.time - start) / (end - start)) * 800)).toFixed(2)},${(125 - ((p.rates.find((x) => x.minutes === m)?.importsPerMinute || 0) / max) * 110).toFixed(2)}`
        );
      }
      if (segment.length) segments.push(segment);
      return segments
        .map(
          (s) =>
            `<polyline points="${s.join(' ')}" fill="none" stroke="${colors[i]}" stroke-width="2" vector-effect="non-scaling-stroke"/>`
        )
        .join('');
    })
    .join(
      ''
    )}</svg><div class="chart-note"><span class="fine">${h(date(start))} — ${h(date(end))}</span><span class="legend">${[1, 5, 15].map((m, i) => `<span><i style="background:${colors[i]}"></i>M${m}</span>`).join('')}</span></div>`;
}
function overview(env, adapters, summary) {
  const data = env.data,
    s = summary.states,
    total = Object.values(s).reduce((a, b) => a + b, 0),
    remaining = (s.pending || 0) + (s.retry || 0) + (s.partial || 0) + (s.importing || 0);
  const loops = (data.loops || []).filter((x) => !state.provider || x.provider === state.provider),
    active = loops.filter((x) => x.state === 'running' && x.jobId);
  const runs = (data.discovery || []).filter(
    (x) => !state.provider || x.provider === state.provider
  );
  const r15 = summary.rates.find((x) => x.minutes === 15),
    discoveryFinished = runs.length > 0 && runs.every((x) => x.state === 'completed');
  const eta =
    discoveryFinished && remaining > 0 && r15?.fullWindow && r15.importsPerMinute > 0
      ? age((remaining / r15.importsPerMinute) * 60)
      : 'Недостаточно данных';
  return `<div class="two-columns"><section class="panel"><div class="panel-header"><div><h2>Скорость импорта</h2><p class="fine">Первый успешный импорт · карточек / минуту</p></div><select id="history-window" aria-label="Период истории">${[
    [1, 'За час'],
    [6, 'За 6 часов'],
    [24, 'За сутки'],
    [168, 'За неделю'],
  ]
    .map(([v, t]) => `<option value="${v}" ${state.hours === v ? 'selected' : ''}>${t}</option>`)
    .join(
      ''
    )}</select></div>${ratesMarkup(summary.rates)}${chartMarkup()}<div class="gap-top"><p class="fine">Обновления существующих карточек / минуту</p><div class="gap-top">${ratesMarkup(summary.rates, 'refreshesPerMinute')}</div></div></section>
  <section class="panel"><div class="panel-header"><h2>Состояние обработки</h2>${badge(data.workersEnabled ? 'Обработчики включены' : 'Обработчики выключены', data.workersEnabled ? '' : 'off')}</div>${kv(
    [
      ['Успешно сейчас', n(s.completed || 0)],
      ['Всего заданий', n(total)],
      ['Осталось обработать', n(remaining)],
      ['Частичные результаты', n(s.partial || 0)],
      ['Отменено', n(s.cancelled || 0)],
      ['Оценка остатка', eta],
    ]
  )}<div class="progress-track"><div class="progress-fill" style="width:${total ? Math.min(100, (100 * (s.completed || 0)) / total) : 0}%"></div></div><p class="fine">${total ? n((100 * (s.completed || 0)) / total, 1) + '% заданий в успешном состоянии' : 'Заданий пока нет'}. Повторные обновления могут менять состояния очереди.</p></section></div>
  <div class="two-columns"><section class="panel"><div class="panel-header"><h2>Сейчас выполняется</h2><span class="count">${n(active.length)}</span></div>${active.length ? active.map((x) => `<div class="active-job"><strong><button class="job-link" data-job="${h(x.externalId ? x.jobId : '')}" ${x.externalId ? '' : 'disabled'}>${h(x.provider)} / ${h(x.externalId || 'обход')}</button></strong>${badge(label(x.stage))}<p class="fine">Heartbeat: ${h(date(x.heartbeatUtc))} · попытка ${h(x.attemptId?.slice(0, 8) || '—')}</p></div>`).join('') : empty(data.workersEnabled ? 'Нет активных заданий' : 'Фоновая обработка выключена', 'Мониторинг продолжает работать.')}</section>
  <section class="panel"><div class="panel-header"><h2>Обход источников</h2></div>${
    runs.length
      ? runs
          .map(
            (x) =>
              `<div class="active-job"><strong>${h(x.provider)} · ${h(label(x.state))}</strong>${kv(
                [
                  ['Найдено ID', n(x.discovered)],
                  ['Страниц', n(x.pages)],
                  ['Ограничение', x.maximumItems ? n(x.maximumItems) : 'Без ограничения'],
                  ['Остаток страницы', n(x.pendingItems)],
                  ['Последний прогресс', date(x.updatedUtc)],
                ]
              )}${x.state !== 'completed' ? '<p class="fine">Обход продолжается: окончательный объём неизвестен.</p>' : ''}${x.error ? `<p class="error">${h(x.error)}</p>` : ''}</div>`
          )
          .join('')
      : empty(
          'Обход не запускался',
          'Список карточек можно получать обходом или передавать отдельными заданиями.'
        )
  }</section></div>
  <section class="panel"><div class="panel-header"><h2>Ресурсы окружения</h2><span class="fine">Процесс CatalogWeaver</span></div><div class="cards">${card('Память', bytes(data.resources?.memoryBytes), 'Рабочий набор процесса')}${card('CPU', n(data.resources?.cpuPercent, 1) + '%', '100% = одно логическое ядро')}${card('Свободно на диске', bytes(data.resources?.diskFreeBytes), 'Файловая система медиа')}${card('Служебная БД', bytes(data.storage?.databaseBytes), 'Очередь, карточки и история')}</div>${kv(
    [
      ['Файлы медиа', n(data.storage?.files) + ' / ' + bytes(data.storage?.mediaBytes)],
      ['Незавершённые публикации', n(data.storage?.publications)],
      ['Экспортные резервы', n(data.storage?.exportReservations)],
      ['Процесс работает', age(data.resources?.uptimeSeconds)],
      ['Начало измерений', date(data.sinceUtc)],
    ]
  )}</section>`;
}
function queueView() {
  const jobs = state.jobs;
  return `<section class="panel"><div class="panel-header"><h2>Задания</h2><div class="filter-row"><label for="job-state">Состояние</label><select id="job-state"><option value="">Все</option>${['pending', 'importing', 'completed', 'partial', 'retry', 'failed', 'cancelled'].map((s) => `<option value="${s}" ${state.jobState === s ? 'selected' : ''}>${h(label(s))}</option>`).join('')}</select></div></div>${
    jobs.length
      ? table(
          [
            'Карточка / задание',
            'Состояние',
            'Этап',
            'Попыток',
            'Обновлено',
            'Следующая обработка',
          ],
          jobs.map(
            (j) =>
              `<tr><td><button class="job-link" data-job="${h(j.id)}">${h(j.result?.item?.title || j.provider + ' / ' + j.externalId)}</button><div class="fine">${h(j.provider)} · ${h(j.externalId)} · ${h(j.id.slice(0, 8))}</div></td><td>${badge(label(j.state), j.state === 'failed' ? 'bad' : ['partial', 'retry'].includes(j.state) ? 'warn' : '')}</td>${cell(label(j.stage))}${cell(n(j.attempts))}${cell(date(j.updatedUtc))}${cell(j.refreshEnabled ? date(j.dueUtc) : 'Обновления отключены')}</tr>`
          )
        )
      : empty('Заданий с таким фильтром нет')
  }<div class="pager"><button class="small-button" data-action="previous" ${!state.cursorStack.length ? 'disabled' : ''}>← Назад</button><span class="fine">До 100 заданий на странице</span><button class="small-button" data-action="next" ${!state.nextCursor ? 'disabled' : ''}>Далее →</button></div></section>`;
}
function httpView(env, adapters) {
  const all = adapters.flatMap((x) => x.traffic || []),
    active = (env.data.activeRequests || []).filter(
      (x) => !state.provider || x.provider === state.provider
    );
  return `<div class="cards">${card('Запросов', n(sum(all, 'requests')), 'С начала измерений')}${card('Получено', bytes(sum(all, 'received')), 'Прочитанные тела ответов')}${card('Отправлено', bytes(sum(all, 'sent')), 'Записанные тела запросов')}${card('Выполняется', n(active.length), 'HTTP-запросов сейчас')}</div><section class="panel"><div class="panel-header"><h2>Общий лимитер адаптера</h2></div>${table(
    ['Адаптер', 'Настроено', 'Бюджет / мин', 'Осталось / мин', 'Пауза Retry-After'],
    adapters.map((a) => {
      const gate = a.limiter?.[0],
        used = gate?.reservedMinute || 0;
      return `<tr>${cell(a.name)}${cell(n(a.rateLimit?.requestsPerSecond, 2) + '/с · ' + n(a.rateLimit?.requestsPerMinute) + '/мин')}${cell(n(used))}${cell(a.rateLimit ? n(Math.max(0, a.rateLimit.requestsPerMinute - used)) : '—')}${cell(gate?.blockedUntilUtc ? age((Date.parse(gate.blockedUntilUtc) - Date.now()) / 1000) : 'Нет')}</tr>`;
    })
  )}<p class="fine gap-top">Бюджет показывает разрешения общего лимитера CatalogWeaver. Фактические HTTP-запросы учитываются отдельно. Другие приложения здесь не учитываются.</p></section><section class="panel"><div class="panel-header"><h2>Запросы за последние 15 минут</h2></div>${table(
    ['Адаптер', 'Запросов', 'Ошибок', 'Медиана', '95-й процентиль', 'Получено'],
    adapters.map((a) => {
      const x = a.http?.[0];
      return `<tr>${cell(a.name)}${cell(n(x?.requests || 0))}${cell(n(x?.errors || 0))}${cell(x ? n(x.p50Ms) + ' мс' : '—')}${cell(x ? n(x.p95Ms) + ' мс' : '—')}${cell(bytes(x?.received || 0))}</tr>`;
    })
  )}</section><section class="panel"><div class="panel-header"><h2>Трафик по категориям</h2></div>${
    all.length
      ? table(
          ['Источник', 'Категория', 'Запросов', 'Ошибок', 'Получено', 'Отправлено'],
          all.map(
            (x) =>
              `<tr>${cell(x.provider)}${cell(label(x.category))}${cell(n(x.requests))}${cell(n(x.errors))}${cell(bytes(x.received))}${cell(bytes(x.sent))}</tr>`
          )
        )
      : empty('HTTP-измерений пока нет')
  }<p class="fine gap-top">Считаются тела HTTP, включая частично прочитанные ответы и повторы. Заголовки, TLS и трафик других процессов сюда не входят.</p></section><section class="panel"><div class="panel-header"><h2>Активные передачи</h2></div>${
    active.length
      ? table(
          ['Источник', 'Хост', 'Категория', 'Получено', 'Ожидается', 'Длительность'],
          active.map(
            (x) =>
              `<tr>${cell(x.provider)}${cell(x.host)}${cell(label(x.category))}${cell(bytes(x.received))}${cell(bytes(x.expected))}${cell(age(x.durationMs / 1000))}</tr>`
          )
        )
      : empty('Активных передач нет')
  }</section>`;
}
function imageView(adapters) {
  const assets = adapters.flatMap((a) =>
      (a.assets || []).map((x) => ({ ...x, provider: a.provider }))
    ),
    files = adapters.flatMap((x) => x.files || []);
  return `<div class="cards">${card('Локальных файлов', n(sum(files, 'files')), 'По выбранным адаптерам')}${card('Размер файлов', bytes(sum(files, 'bytes')), 'Зарегистрированные медиа')}${card('Материалов', n(sum(assets, 'count')), 'Локальные файлы и ссылки')}${card('Проблемные материалы', n(adapters.reduce((total, a) => total + (a.items?.[0]?.assetRetryCards || 0), 0)), 'Карточки с повтором получения материалов')}</div><section class="panel"><div class="panel-header"><h2>Материалы и профили</h2></div>${
    assets.length
      ? table(
          ['Источник', 'Роль', 'Состояние', 'Запрошен', 'Получен', 'Количество'],
          assets.map(
            (x) =>
              `<tr>${cell(x.provider)}${cell(x.role)}${cell(label(x.state))}${cell(x.requestedProfile)}${cell(x.actualProfile)}${cell(n(x.count))}</tr>`
          )
        )
      : empty('Сохранённых материалов пока нет')
  }<p class="fine gap-top">Запрошенный и фактический профиль помогают увидеть возврат к оригиналу. Профили уже поставленных заданий сохраняются в их снимках.</p></section>`;
}
function errorsView(env, adapters) {
  const data = env.data,
    rows = (data.httpErrors || []).filter((x) => !state.provider || x.provider === state.provider),
    loops = (data.loops || []).filter(
      (x) => x.lastFailure && (!state.provider || x.lastFailure.provider === state.provider)
    );
  return `<section class="panel"><div class="panel-header"><h2>Проблемы конфигурации и обработчиков</h2><button class="small-button" data-action="export">Скачать снимок</button></div>${adapters.flatMap((a) => (a.errors || []).map((error) => `<div class="event"><span class="fine">${h(a.name)}</span><div class="code">${h(error)}</div></div>`)).join('')}${loops.map((x) => `<div class="event"><span class="fine">${h(date(x.lastFailure.utc))}</span><div><span class="code">${h(x.name)} · ${h(x.lastFailure.error?.code || x.error)}</span><p>${h(x.lastFailure.error?.safeMessage || '')} · ${h(label(x.lastFailure.stage))}</p></div></div>`).join('')}${!adapters.some((x) => x.errors?.length) && !loops.length ? empty('Сбоев конфигурации и обработчиков нет') : ''}${data.collectorError ? `<p class="error">Сборщик: ${h(data.collectorError)}</p>` : ''}${data.droppedTelemetry ? `<p class="error">При переполнении буфера потеряно HTTP-событий: ${n(data.droppedTelemetry)}</p>` : ''}</section><section class="panel"><div class="panel-header"><h2>Последние HTTP-ошибки</h2><span class="fine">До 50 событий · хранение 7 дней</span></div>${rows.length ? rows.map((x) => `<div class="event"><span class="fine">${h(date(x.utc))}</span><div><span class="code">${h(x.error || 'HTTP ' + x.status)}</span><p>${h(x.provider)} · ${h(x.host)} · ${h(label(x.category))}</p></div></div>`).join('') : empty('HTTP-ошибок пока нет')}<p class="fine gap-top">Ошибки отдельных карточек и время следующего повтора доступны во вкладке «Очередь».</p></section>`;
}
function configView(env, adapters) {
  return `<section class="panel"><div class="panel-header"><h2>Этот сервер</h2></div>${kv([
    ['Идентификатор', env.data.serverId],
    ['Версия', env.data.version],
    ['Снимок', date(env.data.updatedUtc)],
    ['Начало измерений', date(env.data.sinceUtc)],
    ['История графиков', '30 дней'],
    ['HTTP-события', '7 дней'],
    ['Счётчики трафика', 'Сохраняются между перезапусками'],
  ])}</section>${adapters
    .map(
      (a) =>
        `<section class="panel"><div class="panel-header"><h2>${h(a.name)}</h2>${badge(a.enabled ? 'Включён' : 'Выключен', a.enabled ? '' : 'off')}</div>${kv(
          [
            ['Идентификатор', a.provider],
            ['Тип', a.kind],
            ['Версия конфигурации', a.version],
            ['Проверка конфигурации', date(a.checkedUtc)],
            ['Обновление карточек', a.refreshHours ? a.refreshHours + ' ч' : '—'],
            ['Назначение БД', a.destination || 'Внутренний каталог'],
            ['Карточек во внутреннем каталоге', n(a.items?.[0]?.cards || 0)],
            ['Карточек с неполными полями', n(a.items?.[0]?.incompleteFields || 0)],
            ['Карточек с проблемными материалами', n(a.items?.[0]?.assetRetryCards || 0)],
            ['Последнее сохранение', date(a.items?.[0]?.lastSavedUtc)],
          ]
        )}</section>`
    )
    .join(
      ''
    )}<section class="panel"><div class="panel-header"><h2>Модули изображений</h2></div><pre>${h(JSON.stringify(env.data.encoders, null, 2))}</pre></section>`;
}
function render() {
  const focus = document.activeElement?.id;
  renderNavigation();
  if (state.env === 'all') {
    $('main').innerHTML = overviewAll();
    return;
  }
  const env = current();
  if (!env) {
    navigate('all');
    return;
  }
  const adapters = selectedAdapters(),
    summary = aggregate(adapters),
    s = summary.states;
  if (state.provider && env.data && !adapters.length) {
    state.provider = null;
  }
  const title = state.provider ? adapters[0]?.name || state.provider : env.name;
  $('breadcrumbs').textContent = `Окружения / ${env.name}${state.provider ? ' / ' + title : ''}`;
  const [status, kind] = health(env);
  let content = `<div class="page-heading"><div><p class="eyebrow">${state.provider ? 'АДАПТЕР / ' + h(env.name.toUpperCase()) : 'ОКРУЖЕНИЕ'}</p><h1>${h(title)}</h1><p class="subtitle">${h(state.provider ? adapters[0]?.provider : env.description || 'Состояние сервиса и всех его адаптеров')}</p></div><div class="heading-actions">${badge(status, kind)}${env.id !== 'local' ? `<button class="small-button" data-action="edit">Настроить</button><button class="small-button" data-action="delete">Удалить</button>` : ''}</div></div>`;
  if (kind === 'bad' || kind === 'warn')
    content += `<div class="notice ${kind === 'bad' ? 'error' : ''}">${h(env.error ? 'Нет связи с сервером: ' + env.error + '. Отображается последний полученный снимок.' : env.data?.collectorError ? 'Ошибка сборщика: ' + env.data.collectorError : warnings(env).length ? warnings(env).join('; ') : status + '. Проверьте ошибки и время снимка.')} Снимок: ${h(date(env.data?.updatedUtc))}.</div>`;
  if (!env.enabled)
    content +=
      '<div class="notice">Опрос этого окружения отключён. Последние данные могут быть устаревшими.</div>';
  if (!env.data) {
    $('main').innerHTML =
      content + empty('Ожидаем данные сервера', 'Подключение проверяется автоматически.');
    return;
  }
  content += `<div class="cards">${card('Успешно', n(s.completed || 0), 'Заданий в успешном состоянии', true)}${card('Ожидают обработки', n((s.pending || 0) + (s.retry || 0) + (s.partial || 0)), 'Включая повторы и частичные результаты')}${card('В работе', n(s.importing || 0), 'Активные задания')}${card('Ошибки', n(s.failed || 0), 'Задания требуют внимания')}</div><nav class="tabs" aria-label="Разделы мониторинга">${[
    ['overview', 'Обзор'],
    ['queue', 'Очередь'],
    ['http', 'Запросы и трафик'],
    ['images', 'Изображения'],
    ['errors', 'Ошибки'],
    ['config', 'Конфигурация'],
  ]
    .map(
      ([id, text]) =>
        `<button class="tab ${state.tab === id ? 'selected' : ''}" data-tab="${id}">${text}</button>`
    )
    .join('')}</nav>`;
  content +=
    state.tab === 'queue'
      ? queueView()
      : state.tab === 'http'
        ? httpView(env, adapters)
        : state.tab === 'images'
          ? imageView(adapters)
          : state.tab === 'errors'
            ? errorsView(env, adapters)
            : state.tab === 'config'
              ? configView(env, adapters)
              : overview(env, adapters, summary);
  $('main').innerHTML = content;
  if (focus) $(focus)?.focus({ preventScroll: true });
}
async function loadSecondary(force = false) {
  if (state.env === 'all' || !current()?.data) return;
  const generation = state.generation;
  if (state.tab === 'overview') {
    const key = state.env + ':' + state.hours;
    if (force || historyKey !== key) {
      historyKey = key;
      try {
        const data = await api(
          `/environments/${encodeURIComponent(state.env)}/history?hours=${state.hours}`
        );
        if (generation !== state.generation) return;
        state.history = data;
        render();
      } catch (error) {
        historyKey = '';
        throw error;
      }
    }
  }
  if (state.tab === 'queue') {
    const key = [state.env, state.provider, state.jobState, state.jobCursor].join(':');
    if (force || jobsKey !== key) {
      jobsKey = key;
      try {
        const data = await api(
          `/environments/${encodeURIComponent(state.env)}/jobs?provider=${encodeURIComponent(state.provider || '')}&state=${encodeURIComponent(state.jobState)}&cursor=${encodeURIComponent(state.jobCursor || '')}`
        );
        if (generation !== state.generation) return;
        state.jobs = data.items || data;
        state.nextCursor = data.nextCursor || null;
        render();
      } catch (error) {
        jobsKey = '';
        throw error;
      }
    }
  }
}
async function refresh(force = false) {
  if (state.loading) return;
  state.loading = true;
  clearTimeout(timer);
  try {
    state.environments = await api('/environments');
    $('refresh-state').textContent = 'Опрос ' + new Date().toLocaleTimeString('ru-RU');
    render();
    await loadSecondary(force);
  } catch (error) {
    $('refresh-state').textContent = 'Нет связи с локальным клиентом';
    toast(error.message);
  } finally {
    state.loading = false;
    timer = setTimeout(() => refresh(true), 5000);
  }
}
function openEnvironment(edit = false) {
  const env = edit ? current() : null;
  $('environment-form').reset();
  $('environment-id').value = env?.id || '';
  $('environment-title').textContent = env ? 'Настройка окружения' : 'Новое окружение';
  $('environment-name').value = env?.name || '';
  $('environment-address').value = env?.address || '';
  $('environment-key').required = !env;
  $('environment-key').placeholder = env
    ? 'Оставьте пустым, чтобы сохранить ключ'
    : 'Ключ с подключаемого сервера';
  $('environment-color').value = env?.color || '#5dbaa6';
  $('environment-interval').value = env?.intervalSeconds || 5;
  $('environment-description').value = env?.description || '';
  $('environment-enabled').checked = env?.enabled ?? true;
  $('environment-timeout').value = env?.timeoutSeconds ?? 8;
  $('environment-minimum').value = Number(
    (env?.minimumFreeBytes ?? 5368709120) / 1073741824
  ).toFixed(1);
  $('environment-stall').value = env?.noProgressMinutes ?? 15;
  $('environment-error').textContent = '';
  $('save-environment').textContent = env ? 'Проверить и сохранить' : 'Проверить и подключить';
  $('environment-dialog').showModal();
}
async function openJob(id) {
  const env = state.env;
  $('detail-title').textContent = 'Задание';
  $('detail-content').innerHTML = empty('Загрузка…');
  $('detail-dialog').showModal();
  try {
    const job = await api(
      `/environments/${encodeURIComponent(env)}/jobs/${encodeURIComponent(id)}`
    );
    $('detail-title').textContent =
      job.result?.item?.title || `${job.provider} / ${job.externalId}`;
    $('detail-content').innerHTML =
      kv([
        ['ID задания', job.id],
        ['Состояние', label(job.state)],
        ['Этап', label(job.stage)],
        ['Попыток', n(job.attempts)],
        ['Версия адаптера', job.adapterVersion],
        ['Создано', date(job.createdUtc)],
        ['Обновлено', date(job.updatedUtc)],
        ['Следующая обработка', date(job.dueUtc)],
      ]) + '<h3>Поля, материалы и диагностика</h3><pre id="job-result"></pre>';
    $('job-result').textContent = JSON.stringify(job.result, null, 2) || 'Результата пока нет.';
  } catch (error) {
    $('detail-content').textContent = error.message;
  }
}
$('environment-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  const button = $('save-environment');
  button.disabled = true;
  $('environment-error').textContent = '';
  const id = $('environment-id').value;
  try {
    const result = await api('/environments' + (id ? '/' + encodeURIComponent(id) : ''), {
      method: id ? 'PUT' : 'POST',
      body: JSON.stringify({
        name: $('environment-name').value,
        address: $('environment-address').value,
        key: $('environment-key').value,
        color: $('environment-color').value,
        description: $('environment-description').value,
        intervalSeconds: Number($('environment-interval').value),
        enabled: $('environment-enabled').checked,
        timeoutSeconds: Number($('environment-timeout').value),
        minimumFreeBytes: Math.round(Number($('environment-minimum').value) * 1073741824),
        noProgressMinutes: Number($('environment-stall').value),
      }),
    });
    $('environment-key').value = '';
    $('environment-dialog').close();
    await refresh();
    navigate(result.id);
    toast('Подключение сохранено.');
  } catch (error) {
    $('environment-error').textContent = error.message;
  } finally {
    button.disabled = false;
  }
});
document.addEventListener('click', async (event) => {
  const button = event.target.closest('button');
  if (!button) return;
  try {
    if (button.hasAttribute('data-close')) {
      button.closest('dialog').close();
      return;
    }
    if (button.dataset.env) {
      navigate(button.dataset.env);
      return;
    }
    if (button.hasAttribute('data-provider')) {
      navigate(state.env, button.dataset.provider || null);
      return;
    }
    if (button.dataset.tab) {
      navigate(state.env, state.provider, button.dataset.tab);
      return;
    }
    if (button.dataset.job) {
      await openJob(button.dataset.job);
      return;
    }
    const action = button.dataset.action;
    if (action === 'add') openEnvironment();
    if (action === 'edit') openEnvironment(true);
    if (action === 'delete') {
      const env = current();
      if (confirm(`Удалить подключение «${env.name}»? Данные на этом сервере сохранятся.`)) {
        await api('/environments/' + encodeURIComponent(env.id), { method: 'DELETE' });
        navigate('all');
        await refresh();
      }
    }
    if (action === 'export') {
      const blob = new Blob([JSON.stringify(current()?.data, null, 2)], {
          type: 'application/json',
        }),
        url = URL.createObjectURL(blob),
        a = document.createElement('a');
      a.href = url;
      a.download = 'catalogweaver-monitor.json';
      a.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
    if (action === 'next' && state.nextCursor) {
      state.generation++;
      state.cursorStack.push(state.jobCursor);
      state.jobCursor = state.nextCursor;
      await loadSecondary(true);
    }
    if (action === 'previous' && state.cursorStack.length) {
      state.generation++;
      state.jobCursor = state.cursorStack.pop();
      await loadSecondary(true);
    }
  } catch (error) {
    toast(error.message);
  }
});
document.addEventListener('change', async (event) => {
  try {
    if (event.target.id === 'history-window') {
      state.generation++;
      state.hours = Number(event.target.value);
      await loadSecondary(true);
    }
    if (event.target.id === 'job-state') {
      state.generation++;
      state.jobState = event.target.value;
      state.jobCursor = null;
      state.cursorStack = [];
      await loadSecondary(true);
    }
  } catch (error) {
    toast(error.message);
  }
});
$('add-environment').onclick = () => openEnvironment();
$('all-environments').onclick = () => navigate('all');
$('refresh').onclick = () => refresh(true);
$('client-info').onclick = () => {
  $('detail-title').textContent = 'Веб-клиент на этом ПК';
  $('detail-content').innerHTML =
    '<p>Клиент получает показатели каждого подключённого сервера отдельно.</p><p class="muted gap-top">Подключения и ключи сохраняются на этом ПК. Каждое окружение хранит собственную историю. Серверы окружений не обращаются друг к другу.</p>';
  $('detail-dialog').showModal();
};
document.querySelectorAll('dialog').forEach((dialog) =>
  dialog.addEventListener('close', () => {
    if (dialog.id === 'environment-dialog') $('environment-key').value = '';
    else $('detail-content').replaceChildren();
  })
);
const initial = new URLSearchParams(location.hash.slice(1));
state.env = initial.get('env') || 'all';
state.provider = initial.get('adapter');
state.tab = ['overview', 'queue', 'http', 'images', 'errors', 'config'].includes(initial.get('tab'))
  ? initial.get('tab')
  : 'overview';
window.addEventListener('hashchange', () => {
  const params = new URLSearchParams(location.hash.slice(1));
  const env = params.get('env') || 'all',
    provider = params.get('adapter'),
    tab = params.get('tab') || 'overview';
  if (env !== state.env || provider !== state.provider || tab !== state.tab)
    navigate(env, provider, tab);
});
refresh();
