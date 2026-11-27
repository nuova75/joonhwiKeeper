import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { WebSocket } from 'ws';
import { buildApp, type App } from '../src/app.js';
import { config } from '../src/config.js';
import { nullNotifier } from '../src/notify/notifier.js';
import { AgentMessageSchema, PolicySchema } from '../src/protocol.js';

let ctx: App;
let base: string;

before(async () => {
  ctx = await buildApp({ ...config, dbPath: ':memory:' }, { notifier: nullNotifier, logger: false });
  await ctx.app.listen({ port: 0, host: '127.0.0.1' });
  const addr = ctx.app.server.address();
  if (!addr || typeof addr === 'string') throw new Error('no address');
  base = `127.0.0.1:${addr.port}`;
});
after(async () => ctx.app.close());

async function api(method: string, path: string, body?: unknown, token?: string) {
  const res = await fetch(`http://${base}${path}`, {
    method,
    headers: {
      ...(body ? { 'content-type': 'application/json' } : {}),
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: res.status, json: (await res.json()) as any };
}

function nextMessage(ws: WebSocket): Promise<any> {
  return new Promise((resolve) => ws.once('message', (d) => resolve(JSON.parse(d.toString()))));
}

test('shared fixtures match protocol schema', () => {
  const policyMsg = JSON.parse(readFileSync(new URL('../../shared/protocol/fixtures/policy.json', import.meta.url), 'utf8'));
  PolicySchema.parse(policyMsg.policy);
  const msgs = JSON.parse(readFileSync(new URL('../../shared/protocol/fixtures/agent-messages.json', import.meta.url), 'utf8'));
  for (const m of msgs) AgentMessageSchema.parse(m);
});

test('setup → pair → realtime lock push → usage report', async () => {
  const setup = await api('POST', '/api/setup', { password: 'family-secret-1' });
  assert.equal(setup.status, 200);
  const parent = setup.json.token as string;

  assert.equal((await api('POST', '/api/setup', { password: 'other-pass-123' })).status, 409);
  assert.equal((await api('POST', '/api/login', { password: 'wrong-password' })).status, 401);
  assert.equal((await api('GET', '/api/devices')).status, 401);

  const code = await api('POST', '/api/devices/pairing-codes', { name: '첫째 폰', platform: 'android' }, parent);
  assert.match(code.json.code, /^\d{6}$/);

  // 플랫폼 불일치는 거부되고 코드도 소모됨
  const pair = await api('POST', '/api/agent/pair', { code: code.json.code, platform: 'android', agentVersion: '0.1.0' });
  assert.equal(pair.status, 200);
  const { deviceId, token } = pair.json;
  assert.equal((await api('POST', '/api/agent/pair', { code: code.json.code, platform: 'android', agentVersion: '0.1.0' })).status, 400);

  const ws = new WebSocket(`ws://${base}/ws/agent`, { headers: { authorization: `Bearer ${token}` } });
  await new Promise((r) => ws.once('open', r));
  const first = nextMessage(ws);
  ws.send(JSON.stringify({ type: 'hello', agentVersion: '0.1.0', policyVersion: 0 }));
  assert.equal((await first).policy.version, 1);

  const pushed = nextMessage(ws);
  const lock = await api('POST', `/api/devices/${deviceId}/lock`, { locked: true }, parent);
  assert.equal(lock.json.manualLock, true);
  const msg = await pushed;
  assert.equal(msg.type, 'policy');
  assert.equal(msg.policy.manualLock, true);
  assert.equal(msg.policy.version, 2);

  const pushed2 = nextMessage(ws);
  await api('POST', `/api/devices/${deviceId}/extra-time`, { minutes: 30 }, parent);
  assert.equal((await pushed2).policy.extraTime.minutes, 30);

  ws.send(JSON.stringify({ type: 'usage', date: '2026-11-25', usedMinutes: 42, perApp: { a: 42 }, locked: true, lockReason: 'manual' }));
  await new Promise((r) => setTimeout(r, 50));
  const devices = await api('GET', '/api/devices', undefined, parent);
  assert.equal(devices.json[0].online, true);
  assert.equal(devices.json[0].usage.usedMinutes, 42);

  const bad = await api('PUT', `/api/devices/${deviceId}/policy`, { dailyLimitMinutes: 5000 }, parent);
  assert.equal(bad.status, 400);

  ws.close();
});

test('study mode: defaults, child request → parent approve → policy push, daily summary', async () => {
  const parent = (await api('POST', '/api/login', { password: 'family-secret-1' })).json.token as string;
  const code = await api('POST', '/api/devices/pairing-codes', { name: '공부 PC', platform: 'windows' }, parent);
  const pair = await api('POST', '/api/agent/pair', { code: code.json.code, platform: 'windows', agentVersion: '0.2.0' });
  const { deviceId, token, policy } = pair.json;
  // 플랫폼별 기본 학습 허용 목록
  assert.deepEqual(policy.study.apps, ['chrome.exe', 'msedge.exe']);
  assert.ok(policy.study.sites.includes('eunjae.co.kr'));

  const ws = new WebSocket(`ws://${base}/ws/agent`, { headers: { authorization: `Bearer ${token}` } });
  await new Promise((r) => ws.once('open', r));
  ws.send(JSON.stringify({ type: 'hello', agentVersion: '0.2.0', policyVersion: policy.version }));

  // 시간표 저장: 사이트는 정규화되고, 잘못된 형식은 거부
  const savedPush = nextMessage(ws);
  const saved = await api('PUT', `/api/devices/${deviceId}/policy`, {
    study: { apps: ['chrome.exe'], sites: ['*.eunjae.co.kr', 'https://www.ebsi.co.kr/'], schedule: [{ days: [1, 2, 3], start: '19:00', end: '21:00' }] },
  }, parent);
  assert.equal(saved.status, 200);
  assert.deepEqual(saved.json.study.sites, ['eunjae.co.kr', 'ebsi.co.kr']);
  assert.equal((await api('PUT', `/api/devices/${deviceId}/policy`, { study: { apps: [], sites: ['*ebs*'], schedule: [] } }, parent)).status, 400);
  assert.equal((await api('PUT', `/api/devices/${deviceId}/policy`, { study: { apps: [], sites: [], schedule: [{ days: [1], start: '21:00', end: '19:00' }] } }, parent)).status, 400);
  await savedPush;

  // 아이가 공부 시작 → studySession(by child)
  const started = nextMessage(ws);
  ws.send(JSON.stringify({ type: 'study_start', minutes: 60 }));
  const s1 = await started;
  assert.equal(s1.policy.studySession.by, 'child');

  // 잘못된 사이트 요청은 즉시 invalid 응답
  const invalid = nextMessage(ws);
  ws.send(JSON.stringify({ type: 'access_request', kind: 'site', value: '나무위키' }));
  assert.equal((await invalid).decision, 'invalid');

  // 요청 → 중복 요청은 하나로
  ws.send(JSON.stringify({ type: 'access_request', kind: 'site', value: 'https://www.youtube.com/watch?v=1', note: '강의 영상' }));
  ws.send(JSON.stringify({ type: 'access_request', kind: 'site', value: 'youtube.com' }));
  await new Promise((r) => setTimeout(r, 80));
  const pending = await api('GET', '/api/requests', undefined, parent);
  assert.equal(pending.json.length, 1);
  assert.equal(pending.json[0].value, 'youtube.com');
  assert.equal(pending.json[0].deviceName, '공부 PC');

  // 오늘만 허용 → grants 에 들어간 정책 push + 결과 메시지
  const got: any[] = [];
  const collect = new Promise<void>((resolve) => {
    ws.on('message', (d) => { got.push(JSON.parse(d.toString())); if (got.length === 2) resolve(); });
  });
  const ok = await api('POST', `/api/requests/${pending.json[0].id}`, { decision: 'today' }, parent);
  assert.equal(ok.status, 200);
  await collect;
  ws.removeAllListeners('message');
  const pol = got.find((m) => m.type === 'policy').policy;
  assert.deepEqual(pol.grants.sites, ['youtube.com']);
  assert.equal(got.find((m) => m.type === 'request_result').decision, 'today');
  // 두 번째 처리 시도는 409
  assert.equal((await api('POST', `/api/requests/${pending.json[0].id}`, { decision: 'deny' }, parent)).status, 409);

  // 부모가 학습 종료
  const ended = await api('POST', `/api/devices/${deviceId}/study`, { minutes: 0 }, parent);
  assert.equal(ended.json.studySession, null);

  // 하루 요약: 사용 기록이 있는 기기만, 하루 한 번
  const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Seoul', year: 'numeric', month: '2-digit', day: '2-digit' }).format(new Date());
  ws.send(JSON.stringify({ type: 'usage', date: today, usedMinutes: 150, studyMinutes: 100, perApp: {}, locked: false, lockReason: null, mode: 'free' }));
  ws.send(JSON.stringify({ type: 'event', kind: 'process_killed', detail: 'discord.exe', at: `${today}T19:30:00+09:00` }));
  await new Promise((r) => setTimeout(r, 80));
  const sent: string[] = [];
  const notifier = { ...nullNotifier, notifyParents: async (t: string, b: string) => { sent.push(`${t}|${b}`); } };
  const { sendDailySummaries } = await import('../src/watchdog.js');
  const late = new Date(`${today}T23:59:00+09:00`);
  sendDailySummaries({ repo: ctx.repo, notifier, config: { ...config, summaryTime: '21:30' } }, late);
  sendDailySummaries({ repo: ctx.repo, notifier, config: { ...config, summaryTime: '21:30' } }, late);
  const mine = sent.filter((s) => s.startsWith('공부 PC'));
  assert.equal(mine.length, 1);
  assert.match(mine[0]!, /학습 100분 · 자유 50분 · 차단 1회/);
  ws.close();
});

test('applyDecision: always adds to study list once', async () => {
  const { applyDecision } = await import('../src/routes/parent.js');
  const { defaultPolicy } = await import('../src/protocol.js');
  const p0 = defaultPolicy('android');
  const p1 = applyDecision(p0, { kind: 'app', value: 'com.ebs.app' }, 'always', '2026-11-25');
  const p2 = applyDecision(p1, { kind: 'app', value: 'com.ebs.app' }, 'always', '2026-11-25');
  assert.equal(p2.study.apps.filter((a) => a === 'com.ebs.app').length, 1);
  assert.equal(applyDecision(p0, { kind: 'site', value: 'x.com' }, 'deny', '2026-11-25'), p0);
});
