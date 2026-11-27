// xKeep 부모 PWA — 빌드 도구 없이 동작하는 단일 모듈.
const $ = (sel, root = document) => root.querySelector(sel);
const TOKEN_KEY = 'xkeep.parentToken';
const LOCK_LABEL = { manual: '부모 잠금', daily_limit: '시간 초과', bedtime: '취침 시간', tamper: '⚠️ 변조 감지' };
const EVENT_LABEL = {
  process_killed: '차단 프로그램 종료',
  app_blocked: '차단 앱 실행 시도',
  usage_access_revoked: '⚠️ 사용 기록 권한 해제',
  device_owner_lost: '⚠️ 관리자 권한 상실',
  session_disconnected: '세션 강제 종료',
  lock_started: '잠금 시작',
  lock_ended: '잠금 해제',
  tamper_suspected: '⚠️ 우회 시도 의심',
  study_started: '학습 모드 시작',
  study_ended: '학습 모드 종료',
};
const DAY_NAMES = ['월', '화', '수', '목', '금', '토', '일']; // ISO 1..7
const DECISION_LABEL = { today: '오늘만 허용', always: '항상 허용', deny: '거절' };

let token = safeGet(TOKEN_KEY);
let devices = [];
let editingId = null;

function safeGet(k) { try { return localStorage.getItem(k); } catch { return null; } }
function safeSet(k, v) { try { v == null ? localStorage.removeItem(k) : localStorage.setItem(k, v); } catch {} }

async function api(method, path, body) {
  const res = await fetch(path, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (res.status === 401 && path !== '/api/login') { logout(); throw new Error('로그인이 필요합니다'); }
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.issues?.map((i) => i.message).join('\n') || data.error || `HTTP ${res.status}`);
  return data;
}

function logout() { token = null; safeSet(TOKEN_KEY, null); showAuth(); }

// ---------- 인증 ----------
async function showAuth() {
  $('#view-devices').hidden = true;
  $('#view-auth').hidden = false;
  const { initialized } = await api('GET', '/api/setup/status');
  $('#auth-title').textContent = initialized ? '로그인' : '처음 설정';
  $('#auth-hint').textContent = initialized
    ? '가족 비밀번호를 입력하세요.'
    : '엄마·아빠가 함께 쓸 가족 비밀번호를 정하세요. 이 비밀번호로 두 분 모두 로그인합니다.';
  $('#form-auth').onsubmit = async (e) => {
    e.preventDefault();
    $('#auth-error').textContent = '';
    try {
      const { token: t } = await api('POST', initialized ? '/api/login' : '/api/setup', { password: $('#auth-password').value });
      token = t; safeSet(TOKEN_KEY, t);
      $('#auth-password').value = '';
      start();
    } catch (err) { $('#auth-error').textContent = err.message === 'invalid_password' ? '비밀번호가 틀렸습니다' : err.message; }
  };
}

// ---------- 기기 목록 ----------
async function loadDevices() {
  const [list, reqs] = await Promise.all([api('GET', '/api/devices'), api('GET', '/api/requests')]);
  devices = list;
  render();
  renderRequests(reqs);
}

// ---------- 허용 요청 ----------
function renderRequests(reqs) {
  $('#requests').hidden = !reqs.length;
  $('#request-list').replaceChildren(...reqs.map((r) => {
    const li = document.createElement('li');
    const title = document.createElement('div');
    title.textContent = `${r.deviceName} · ${r.kind === 'site' ? '사이트' : '앱'} ${r.value}`;
    const meta = document.createElement('div');
    meta.className = 'muted';
    meta.textContent = `${r.note ? `"${r.note}" · ` : ''}${new Date(r.createdAt).toLocaleString('ko-KR')}`;
    const actions = document.createElement('div');
    actions.className = 'actions';
    for (const decision of ['today', 'always', 'deny']) {
      const b = document.createElement('button');
      b.textContent = DECISION_LABEL[decision];
      if (decision !== 'today') b.className = 'ghost';
      b.onclick = () => act(() => api('POST', `/api/requests/${r.id}`, { decision }).catch((e) => {
        if (e.message !== 'already_decided') throw e; // 다른 부모가 먼저 처리
      }));
      actions.append(b);
    }
    li.append(title, meta, actions);
    return li;
  }));
}

/** 기기 시간대 기준 현재 요일(ISO 1..7)과 HH:mm */
function localNow(tz) {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-US', {
    timeZone: tz, weekday: 'short', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(new Date()).map((x) => [x.type, x.value]));
  const day = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'].indexOf(parts.weekday) + 1;
  return { day, hhmm: `${parts.hour}:${parts.minute}` };
}
/** 학습 모드 판정 (에이전트의 LimitEvaluator 와 같은 규칙, 화면 표시용) */
function studyState(p) {
  if (p.studySession && new Date(p.studySession.until) > new Date()) return { by: p.studySession.by, until: p.studySession.until };
  const { day, hhmm } = localNow(p.timezone);
  const block = p.study.schedule.find((b) => b.days.includes(day) && b.start <= hhmm && hhmm < b.end);
  return block ? { by: 'schedule', until: block.end } : null;
}

function todayIn(tz) {
  return new Intl.DateTimeFormat('en-CA', { timeZone: tz, year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
}

function render() {
  const root = $('#devices');
  root.replaceChildren();
  if (!devices.length) {
    root.innerHTML = '<p class="muted">등록된 기기가 없습니다. 아래에서 자녀 기기를 추가하세요.</p>';
    return;
  }
  for (const d of devices) {
    const el = $('#tpl-device').content.firstElementChild.cloneNode(true);
    const p = d.policy;
    $('.d-name', el).textContent = d.name;
    $('.d-platform', el).textContent = d.platform === 'android' ? ' · Android' : ' · Windows';
    const online = $('.d-online', el);
    online.textContent = d.online ? '연결됨' : (d.lastSeen ? `마지막 ${new Date(d.lastSeen).toLocaleString('ko-KR')}` : '미연결');
    online.classList.toggle('on', d.online);

    const today = todayIn(p.timezone);
    const isToday = d.usage?.date === today;
    const study = isToday ? d.usage.studyMinutes ?? 0 : 0;
    const used = isToday ? Math.max(0, d.usage.usedMinutes - study) : 0; // 자유 시간
    const extra = p.extraTime?.date === today ? p.extraTime.minutes : 0;
    const limit = p.dailyLimitMinutes == null ? null : p.dailyLimitMinutes + extra;
    $('.d-usage', el).textContent = (limit == null
      ? `자유 ${used}분 (제한 없음)`
      : `자유 ${used}분 / ${limit}분${extra ? ` (추가 ${extra}분 포함)` : ''}`) + ` · 학습 ${study}분`;
    const fill = $('.meter-fill', el);
    fill.style.width = limit ? `${Math.min(100, (used / limit) * 100)}%` : '0';
    fill.classList.toggle('over', limit != null && used >= limit);

    const reason = isToday && d.usage.locked ? LOCK_LABEL[d.usage.lockReason] ?? '잠김' : null;
    $('.d-lock', el).textContent = p.manualLock ? '🔒 부모 잠금 중' : reason ? `🔒 ${reason}` : '';

    const st = studyState(p);
    const mode = $('.d-mode', el);
    const locked = p.manualLock || !!reason;
    mode.textContent = locked ? '잠김' : st ? '📚 학습 중' : '자유';
    mode.classList.toggle('locked', locked);
    mode.classList.toggle('study', !locked && !!st);
    const studyBtn = $('.a-study', el);
    if (st && st.by === 'schedule') {
      studyBtn.textContent = `시간표 학습 ~${st.until}`;
      studyBtn.disabled = true;
    } else if (st) {
      const until = new Date(st.until).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit' });
      studyBtn.textContent = `학습 종료 (~${until})`;
      studyBtn.onclick = () => act(() => api('POST', `/api/devices/${d.id}/study`, { minutes: 0 }));
    } else {
      studyBtn.textContent = '학습 시작';
      studyBtn.onclick = () => {
        const m = Number(prompt('몇 분 동안 학습 모드로 할까요?', '60'));
        if (m > 0) act(() => api('POST', `/api/devices/${d.id}/study`, { minutes: Math.min(480, Math.round(m)) }));
      };
    }

    const lockBtn = $('.a-lock', el);
    lockBtn.textContent = p.manualLock ? '잠금 해제' : '지금 잠금';
    lockBtn.classList.toggle('danger', !p.manualLock);
    lockBtn.onclick = () => act(() => api('POST', `/api/devices/${d.id}/lock`, { locked: !p.manualLock }));
    $('.a-extra', el).onclick = () => act(() => api('POST', `/api/devices/${d.id}/extra-time`, { minutes: 30 }));
    $('.a-policy', el).onclick = () => openPolicy(d);
    $('.a-events', el).onclick = () => openEvents(d);
    root.append(el);
  }
}

async function act(fn) {
  try { await fn(); await loadDevices(); } catch (err) { alert(err.message); }
}

// ---------- 정책 편집 ----------
function openPolicy(d) {
  editingId = d.id;
  const p = d.policy;
  $('#policy-title').textContent = `${d.name} 설정`;
  $('#p-limit').value = p.dailyLimitMinutes ?? '';
  $('#p-bed-on').checked = !!p.bedtime;
  $('#p-bed-start').value = p.bedtime?.start ?? '22:00';
  $('#p-bed-end').value = p.bedtime?.end ?? '07:00';
  $('#p-blocked').value = p.blockedApps.join('\n');
  $('#p-allowed').value = p.alwaysAllowed.join('\n');
  $('#p-study-sites').value = p.study.sites.join('\n');
  $('#p-study-apps').value = p.study.apps.join('\n');
  $('#p-schedule').replaceChildren(...p.study.schedule.map(blockEditor));
  $('#p-apps-list').replaceChildren();
  $('#p-apps-pick').open = false;
  $('#p-apps-pick').ontoggle = () => { if ($('#p-apps-pick').open) loadAppChips(d.id); };
  $('#dlg-policy').showModal();
}

function blockEditor(b = { days: [1, 2, 3, 4, 5], start: '19:00', end: '21:00' }) {
  const el = $('#tpl-block').content.firstElementChild.cloneNode(true);
  const days = $('.days', el);
  DAY_NAMES.forEach((name, i) => {
    const label = document.createElement('label');
    const cb = document.createElement('input');
    cb.type = 'checkbox'; cb.value = String(i + 1); cb.checked = b.days.includes(i + 1);
    label.append(cb, name);
    days.append(label);
  });
  $('.b-start', el).value = b.start;
  $('.b-end', el).value = b.end;
  $('.b-del', el).onclick = () => el.remove();
  return el;
}
$('#p-schedule-add').onclick = () => $('#p-schedule').append(blockEditor());

function readSchedule() {
  return [...$('#p-schedule').children].map((el) => ({
    days: [...el.querySelectorAll('.days input:checked')].map((cb) => Number(cb.value)),
    start: $('.b-start', el).value,
    end: $('.b-end', el).value,
  })).filter((b) => b.days.length);
}

async function loadAppChips(id) {
  const apps = await api('GET', `/api/devices/${id}/apps`);
  const box = $('#p-apps-list');
  if (!apps.length) { box.textContent = '아직 보고된 목록이 없습니다.'; return; }
  box.replaceChildren(...apps.map((a) => {
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'ghost';
    b.textContent = a.label && a.label !== a.id ? `${a.label}` : a.id;
    b.title = a.id;
    b.onclick = () => {
      const ta = $('#p-study-apps');
      const cur = ta.value.split('\n').map((x) => x.trim()).filter(Boolean);
      if (!cur.includes(a.id)) ta.value = [...cur, a.id].join('\n');
      b.disabled = true;
    };
    return b;
  }));
}

$('#dlg-policy').addEventListener('close', async () => {
  if ($('#dlg-policy').returnValue !== 'save' || !editingId) return;
  const lines = (s) => s.split('\n').map((x) => x.trim()).filter(Boolean);
  const limit = $('#p-limit').value.trim();
  await act(() => api('PUT', `/api/devices/${editingId}/policy`, {
    dailyLimitMinutes: limit === '' ? null : Number(limit),
    bedtime: $('#p-bed-on').checked ? { start: $('#p-bed-start').value, end: $('#p-bed-end').value } : null,
    blockedApps: lines($('#p-blocked').value),
    alwaysAllowed: lines($('#p-allowed').value),
    study: { apps: lines($('#p-study-apps').value), sites: lines($('#p-study-sites').value), schedule: readSchedule() },
  }));
});

async function openEvents(d) {
  const list = await api('GET', `/api/devices/${d.id}/events?limit=50`);
  const ul = $('#events');
  ul.replaceChildren(...(list.length ? list : [{ kind: '', detail: '기록 없음', at: '' }]).map((e) => {
    const li = document.createElement('li');
    li.textContent = e.at
      ? `${new Date(e.at).toLocaleString('ko-KR')} · ${EVENT_LABEL[e.kind] ?? e.kind} ${e.detail}`
      : e.detail;
    return li;
  }));
  $('#dlg-events').showModal();
}

// ---------- 페어링 ----------
$('#form-pair').onsubmit = async (e) => {
  e.preventDefault();
  try {
    const { code, expiresAt } = await api('POST', '/api/devices/pairing-codes', {
      name: $('#pair-name').value, platform: $('#pair-platform').value,
    });
    $('#pair-result').innerHTML = '';
    const big = document.createElement('div');
    big.className = 'code'; big.textContent = code;
    const hint = document.createElement('p');
    hint.className = 'muted';
    hint.textContent = `자녀 기기 에이전트에 이 코드를 입력하세요. ${new Date(expiresAt).toLocaleTimeString('ko-KR')}까지 유효합니다.`;
    $('#pair-result').append(big, hint);
  } catch (err) { alert(err.message); }
};

// ---------- 실시간 ----------
let ws;
function connectLive() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(`${proto}://${location.host}/ws/parent?token=${encodeURIComponent(token)}`);
  let timer;
  ws.onmessage = () => { clearTimeout(timer); timer = setTimeout(loadDevices, 300); }; // 짧은 디바운스
  ws.onclose = (e) => { if (e.code !== 4401 && token) setTimeout(connectLive, 3000); };
}

// ---------- 웹 푸시 ----------
async function setupPush() {
  if (!('serviceWorker' in navigator) || !('PushManager' in window)) return;
  const reg = await navigator.serviceWorker.register('/sw.js');
  const { key } = await api('GET', '/api/push/vapid-public-key');
  if (!key) return;
  const existing = await reg.pushManager.getSubscription();
  if (existing) { await api('POST', '/api/push/subscribe', existing.toJSON()); return; }
  const btn = $('#btn-push');
  btn.hidden = false;
  // iOS 는 사용자 동작(탭) 안에서만 권한 요청 가능
  btn.onclick = async () => {
    const perm = await Notification.requestPermission();
    if (perm !== 'granted') return alert('알림이 거부되었습니다. 설정에서 허용하세요.');
    const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: urlB64(key) });
    await api('POST', '/api/push/subscribe', sub.toJSON());
    btn.hidden = true;
  };
}
function urlB64(s) {
  const pad = '='.repeat((4 - (s.length % 4)) % 4);
  const raw = atob((s + pad).replace(/-/g, '+').replace(/_/g, '/'));
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

async function start() {
  $('#view-auth').hidden = true;
  $('#view-devices').hidden = false;
  await loadDevices();
  connectLive();
  setupPush().catch(() => {});
}

if (token) start().catch(() => showAuth()); else showAuth();
