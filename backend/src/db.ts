/**
 * SQLite 저장소. 가족 1세대 규모(기기 수 개)라 단일 파일 DB 로 충분하고,
 * 라즈베리 파이에서도 별도 DB 서버 없이 동작한다.
 */
import Database from 'better-sqlite3';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { randomUUID } from 'node:crypto';
import { defaultPolicy, PolicySchema, type Platform, type Policy } from './protocol.js';

export interface DeviceRow {
  id: string;
  name: string;
  platform: Platform;
  fcm_token: string | null;
  policy_json: string;
  last_seen: number | null;
  graceful_offline: number;
  offline_alerted: number;
  last_usage_json: string | null;
  last_processes_json: string | null;
  apps_json: string | null;
  summary_sent_date: string | null;
  created_at: number;
}

export interface AccessRequestRow {
  id: number;
  device_id: string;
  device_name?: string;
  kind: 'site' | 'app';
  value: string;
  note: string;
  status: 'pending' | 'today' | 'always' | 'deny';
  created_at: number;
  decided_at: number | null;
}

export interface DeviceView {
  id: string;
  name: string;
  platform: Platform;
  policy: Policy;
  lastSeen: number | null;
  usage: unknown;
  processes: string[];
  createdAt: number;
}

const SCHEMA = `
CREATE TABLE IF NOT EXISTS family (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  password_hash TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS parent_sessions (
  token_hash TEXT PRIMARY KEY,
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS devices (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  platform TEXT NOT NULL CHECK (platform IN ('android', 'windows')),
  token_hash TEXT NOT NULL UNIQUE,
  fcm_token TEXT,
  policy_json TEXT NOT NULL,
  last_seen INTEGER,
  graceful_offline INTEGER NOT NULL DEFAULT 0,
  offline_alerted INTEGER NOT NULL DEFAULT 0,
  last_usage_json TEXT,
  last_processes_json TEXT,
  created_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS pairing_codes (
  code TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  platform TEXT NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  detail TEXT NOT NULL,
  at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_events_device ON events(device_id, id DESC);
CREATE TABLE IF NOT EXISTS usage_daily (
  device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  date TEXT NOT NULL,
  minutes INTEGER NOT NULL,
  per_app_json TEXT NOT NULL,
  PRIMARY KEY (device_id, date)
);
CREATE TABLE IF NOT EXISTS access_requests (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  device_id TEXT NOT NULL REFERENCES devices(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('site', 'app')),
  value TEXT NOT NULL,
  note TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  created_at INTEGER NOT NULL,
  decided_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_requests_status ON access_requests(status, id DESC);
CREATE TABLE IF NOT EXISTS push_subscriptions (
  endpoint TEXT PRIMARY KEY,
  json TEXT NOT NULL,
  created_at INTEGER NOT NULL
);
`;

export class Repo {
  readonly db: Database.Database;

  constructor(path: string) {
    if (path !== ':memory:') mkdirSync(dirname(path), { recursive: true });
    this.db = new Database(path);
    this.db.pragma('journal_mode = WAL');
    this.db.pragma('foreign_keys = ON');
    this.db.exec(SCHEMA);
    this.migrate();
  }

  /** 기존 DB 에 새 컬럼 추가 (SQLite 는 ADD COLUMN IF NOT EXISTS 가 없어서 직접 확인) */
  private migrate(): void {
    const addColumn = (table: string, column: string, ddl: string) => {
      const cols = this.db.prepare(`PRAGMA table_info(${table})`).all() as { name: string }[];
      if (!cols.some((c) => c.name === column)) this.db.exec(`ALTER TABLE ${table} ADD COLUMN ${ddl}`);
    };
    addColumn('devices', 'apps_json', 'apps_json TEXT');
    addColumn('devices', 'summary_sent_date', 'summary_sent_date TEXT');
    addColumn('usage_daily', 'study_minutes', 'study_minutes INTEGER NOT NULL DEFAULT 0');
  }

  // ----- family / parent auth -----
  getFamilyPasswordHash(): string | null {
    const row = this.db.prepare('SELECT password_hash FROM family WHERE id = 1').get() as
      | { password_hash: string }
      | undefined;
    return row?.password_hash ?? null;
  }
  createFamily(passwordHash: string): boolean {
    const r = this.db
      .prepare('INSERT OR IGNORE INTO family (id, password_hash, created_at) VALUES (1, ?, ?)')
      .run(passwordHash, Date.now());
    return r.changes === 1;
  }
  addParentSession(tokenHash: string): void {
    this.db.prepare('INSERT INTO parent_sessions (token_hash, created_at) VALUES (?, ?)').run(tokenHash, Date.now());
  }
  hasParentSession(tokenHash: string): boolean {
    return !!this.db.prepare('SELECT 1 FROM parent_sessions WHERE token_hash = ?').get(tokenHash);
  }

  // ----- pairing -----
  createPairingCode(code: string, name: string, platform: Platform, expiresAt: number): void {
    this.db.prepare('DELETE FROM pairing_codes WHERE expires_at < ?').run(Date.now());
    this.db
      .prepare('INSERT INTO pairing_codes (code, name, platform, expires_at) VALUES (?, ?, ?, ?)')
      .run(code, name, platform, expiresAt);
  }
  /** 코드는 1회용: 조회와 동시에 삭제 */
  consumePairingCode(code: string): { name: string; platform: Platform } | null {
    return this.db.transaction(() => {
      const row = this.db
        .prepare('SELECT name, platform, expires_at FROM pairing_codes WHERE code = ?')
        .get(code) as { name: string; platform: Platform; expires_at: number } | undefined;
      if (!row) return null;
      this.db.prepare('DELETE FROM pairing_codes WHERE code = ?').run(code);
      if (row.expires_at < Date.now()) return null;
      return { name: row.name, platform: row.platform };
    })();
  }

  // ----- devices -----
  createDevice(name: string, platform: Platform, tokenHash: string, fcmToken: string | null): DeviceRow {
    const id = randomUUID();
    this.db
      .prepare(
        `INSERT INTO devices (id, name, platform, token_hash, fcm_token, policy_json, created_at)
         VALUES (?, ?, ?, ?, ?, ?, ?)`,
      )
      .run(id, name, platform, tokenHash, fcmToken, JSON.stringify(defaultPolicy(platform)), Date.now());
    return this.getDevice(id)!;
  }
  getDevice(id: string): DeviceRow | null {
    return (this.db.prepare('SELECT * FROM devices WHERE id = ?').get(id) as DeviceRow | undefined) ?? null;
  }
  getDeviceByTokenHash(tokenHash: string): DeviceRow | null {
    return (
      (this.db.prepare('SELECT * FROM devices WHERE token_hash = ?').get(tokenHash) as DeviceRow | undefined) ?? null
    );
  }
  listDevices(): DeviceRow[] {
    return this.db.prepare('SELECT * FROM devices ORDER BY created_at').all() as DeviceRow[];
  }
  deleteDevice(id: string): boolean {
    return this.db.prepare('DELETE FROM devices WHERE id = ?').run(id).changes === 1;
  }
  setFcmToken(id: string, token: string): void {
    this.db.prepare('UPDATE devices SET fcm_token = ? WHERE id = ?').run(token, id);
  }

  getPolicy(id: string): Policy | null {
    const d = this.getDevice(id);
    return d ? PolicySchema.parse(JSON.parse(d.policy_json)) : null;
  }
  /** 정책 변경은 항상 이 함수로: version 을 원자적으로 1 증가시킨다 */
  updatePolicy(id: string, mutate: (p: Policy) => Policy): Policy | null {
    return this.db.transaction(() => {
      const current = this.getPolicy(id);
      if (!current) return null;
      const next = PolicySchema.parse({ ...mutate(current), version: current.version + 1 });
      this.db.prepare('UPDATE devices SET policy_json = ? WHERE id = ?').run(JSON.stringify(next), id);
      return next;
    })();
  }

  markSeen(id: string): void {
    this.db
      .prepare('UPDATE devices SET last_seen = ?, graceful_offline = 0, offline_alerted = 0 WHERE id = ?')
      .run(Date.now(), id);
  }
  markGracefulOffline(id: string): void {
    this.db.prepare('UPDATE devices SET graceful_offline = 1 WHERE id = ?').run(id);
  }
  markOfflineAlerted(id: string): void {
    this.db.prepare('UPDATE devices SET offline_alerted = 1 WHERE id = ?').run(id);
  }
  saveUsage(
    id: string,
    usage: { date: string; usedMinutes: number; studyMinutes: number; perApp: Record<string, number> },
    raw: unknown,
  ) {
    this.db.transaction(() => {
      this.db.prepare('UPDATE devices SET last_usage_json = ? WHERE id = ?').run(JSON.stringify(raw), id);
      this.db
        .prepare(
          `INSERT INTO usage_daily (device_id, date, minutes, study_minutes, per_app_json) VALUES (?, ?, ?, ?, ?)
           ON CONFLICT(device_id, date) DO UPDATE SET minutes = excluded.minutes,
             study_minutes = excluded.study_minutes, per_app_json = excluded.per_app_json`,
        )
        .run(id, usage.date, usage.usedMinutes, usage.studyMinutes, JSON.stringify(usage.perApp));
    })();
  }
  saveApps(id: string, apps: { id: string; label: string }[]): void {
    this.db.prepare('UPDATE devices SET apps_json = ? WHERE id = ?').run(JSON.stringify(apps), id);
  }
  getApps(id: string): { id: string; label: string }[] {
    const row = this.db.prepare('SELECT apps_json FROM devices WHERE id = ?').get(id) as { apps_json: string | null } | undefined;
    return row?.apps_json ? (JSON.parse(row.apps_json) as { id: string; label: string }[]) : [];
  }
  setSummarySent(id: string, date: string): void {
    this.db.prepare('UPDATE devices SET summary_sent_date = ? WHERE id = ?').run(date, id);
  }
  /** 해당 날짜(기기 현지 날짜)의 차단 횟수. 이벤트 at 은 기기 현지 오프셋이 붙은 ISO 문자열 */
  countBlocked(id: string, date: string): number {
    const row = this.db
      .prepare(
        `SELECT COUNT(*) AS n FROM events WHERE device_id = ? AND substr(at, 1, 10) = ?
         AND kind IN ('process_killed', 'app_blocked')`,
      )
      .get(id, date) as { n: number };
    return row.n;
  }
  saveProcesses(id: string, names: string[]): void {
    this.db.prepare('UPDATE devices SET last_processes_json = ? WHERE id = ?').run(JSON.stringify(names), id);
  }
  getUsageHistory(id: string, days: number) {
    return this.db
      .prepare(
        'SELECT date, minutes, study_minutes, per_app_json FROM usage_daily WHERE device_id = ? ORDER BY date DESC LIMIT ?',
      )
      .all(id, days)
      .map((r) => {
        const row = r as { date: string; minutes: number; study_minutes: number; per_app_json: string };
        return {
          date: row.date,
          minutes: row.minutes,
          studyMinutes: row.study_minutes,
          blocked: this.countBlocked(id, row.date),
          perApp: JSON.parse(row.per_app_json) as Record<string, number>,
        };
      });
  }

  // ----- access requests -----
  /** 같은 내용의 대기 중 요청이 있으면 새로 만들지 않고 null */
  addRequest(deviceId: string, kind: 'site' | 'app', value: string, note: string): AccessRequestRow | null {
    return this.db.transaction(() => {
      const dup = this.db
        .prepare(`SELECT 1 FROM access_requests WHERE device_id = ? AND kind = ? AND value = ? AND status = 'pending'`)
        .get(deviceId, kind, value);
      if (dup) return null;
      const pending = this.db
        .prepare(`SELECT COUNT(*) AS n FROM access_requests WHERE device_id = ? AND status = 'pending'`)
        .get(deviceId) as { n: number };
      if (pending.n >= 30) return null; // 요청 폭주 방지
      const r = this.db
        .prepare('INSERT INTO access_requests (device_id, kind, value, note, created_at) VALUES (?, ?, ?, ?, ?)')
        .run(deviceId, kind, value, note, Date.now());
      return this.getRequest(Number(r.lastInsertRowid));
    })();
  }
  getRequest(id: number): AccessRequestRow | null {
    return (
      (this.db.prepare('SELECT * FROM access_requests WHERE id = ?').get(id) as AccessRequestRow | undefined) ?? null
    );
  }
  listRequests(status: 'pending' | 'all', limit = 100): AccessRequestRow[] {
    const where = status === 'pending' ? `WHERE r.status = 'pending'` : '';
    return this.db
      .prepare(
        `SELECT r.*, d.name AS device_name FROM access_requests r JOIN devices d ON d.id = r.device_id
         ${where} ORDER BY r.id DESC LIMIT ?`,
      )
      .all(limit) as AccessRequestRow[];
  }
  /** pending 인 요청만 결정 가능. 이미 처리된 요청이면 false (엄마·아빠 동시 처리 대비) */
  decideRequest(id: number, decision: 'today' | 'always' | 'deny'): boolean {
    return (
      this.db
        .prepare(`UPDATE access_requests SET status = ?, decided_at = ? WHERE id = ? AND status = 'pending'`)
        .run(decision, Date.now(), id).changes === 1
    );
  }

  // ----- events -----
  addEvent(deviceId: string, kind: string, detail: string, at: string): void {
    this.db.prepare('INSERT INTO events (device_id, kind, detail, at) VALUES (?, ?, ?, ?)').run(deviceId, kind, detail, at);
  }
  listEvents(deviceId: string, limit: number) {
    return this.db
      .prepare('SELECT kind, detail, at FROM events WHERE device_id = ? ORDER BY id DESC LIMIT ?')
      .all(deviceId, limit) as { kind: string; detail: string; at: string }[];
  }

  // ----- web push -----
  savePushSubscription(endpoint: string, json: string): void {
    this.db
      .prepare(
        `INSERT INTO push_subscriptions (endpoint, json, created_at) VALUES (?, ?, ?)
         ON CONFLICT(endpoint) DO UPDATE SET json = excluded.json`,
      )
      .run(endpoint, json, Date.now());
  }
  listPushSubscriptions(): { endpoint: string; json: string }[] {
    return this.db.prepare('SELECT endpoint, json FROM push_subscriptions').all() as { endpoint: string; json: string }[];
  }
  deletePushSubscription(endpoint: string): void {
    this.db.prepare('DELETE FROM push_subscriptions WHERE endpoint = ?').run(endpoint);
  }

  toView(d: DeviceRow): DeviceView {
    return {
      id: d.id,
      name: d.name,
      platform: d.platform,
      policy: PolicySchema.parse(JSON.parse(d.policy_json)),
      lastSeen: d.last_seen,
      usage: d.last_usage_json ? JSON.parse(d.last_usage_json) : null,
      processes: d.last_processes_json ? (JSON.parse(d.last_processes_json) as string[]) : [],
      createdAt: d.created_at,
    };
  }
}
