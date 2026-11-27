/**
 * 부모용 REST API + 부모 WebSocket.
 * 인증: 가족 비밀번호로 로그인 → 세션 토큰(Bearer). 부모 2명이 같은 비밀번호를 쓴다.
 */
import type { FastifyInstance, FastifyReply, FastifyRequest } from 'fastify';
import { z } from 'zod';
import { bearer, hashPassword, hashToken, newPairingCode, newToken, verifyPassword } from '../auth.js';
import type { Config } from '../config.js';
import type { Repo } from '../db.js';
import { PlatformSchema, PolicyUpdateSchema, type Policy, type RequestDecision } from '../protocol.js';
import type { Hub } from '../ws/hub.js';

export function todayIn(timezone: string, now = new Date()): string {
  // en-CA 로케일은 YYYY-MM-DD 형식
  return new Intl.DateTimeFormat('en-CA', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

/** 요청 승인 결과를 정책에 반영. today = 오늘만(grants), always = 학습 허용 목록에 추가 */
export function applyDecision(
  policy: Policy,
  req: { kind: 'site' | 'app'; value: string },
  decision: RequestDecision,
  today: string,
): Policy {
  if (decision === 'deny') return policy;
  const add = (list: string[]) => (list.includes(req.value) ? list : [...list, req.value]);
  if (decision === 'always') {
    const study = { ...policy.study };
    if (req.kind === 'site') study.sites = add(study.sites);
    else study.apps = add(study.apps);
    return { ...policy, study };
  }
  const g = policy.grants?.date === today ? policy.grants : { date: today, apps: [], sites: [] };
  return {
    ...policy,
    grants: {
      date: today,
      apps: req.kind === 'app' ? add(g.apps) : g.apps,
      sites: req.kind === 'site' ? add(g.sites) : g.sites,
    },
  };
}

/** 로그인 무차별 대입 방지: 15분에 실패 10회 초과 시 차단 (메모리 기반, 단일 프로세스용) */
class LoginLimiter {
  private failures = new Map<string, number[]>();
  blocked(key: string): boolean {
    const cutoff = Date.now() - 15 * 60_000;
    const list = (this.failures.get(key) ?? []).filter((t) => t > cutoff);
    this.failures.set(key, list);
    return list.length >= 10;
  }
  fail(key: string): void {
    this.failures.set(key, [...(this.failures.get(key) ?? []), Date.now()]);
  }
}

export async function parentRoutes(app: FastifyInstance, deps: { repo: Repo; hub: Hub; config: Config }) {
  const { repo, hub, config } = deps;
  const limiter = new LoginLimiter();

  const requireParent = async (req: FastifyRequest, reply: FastifyReply) => {
    const token = bearer(req.headers.authorization);
    if (!token || !repo.hasParentSession(hashToken(token))) {
      return reply.code(401).send({ error: 'unauthorized' });
    }
  };

  const Password = z.object({ password: z.string().min(8).max(200) });

  app.get('/api/setup/status', async () => ({ initialized: repo.getFamilyPasswordHash() !== null }));

  /** 최초 1회: 가족 비밀번호 설정 */
  app.post('/api/setup', async (req, reply) => {
    const { password } = Password.parse(req.body);
    if (!repo.createFamily(await hashPassword(password))) return reply.code(409).send({ error: 'already_initialized' });
    const token = newToken();
    repo.addParentSession(hashToken(token));
    return { token };
  });

  app.post('/api/login', async (req, reply) => {
    const key = req.ip;
    if (limiter.blocked(key)) return reply.code(429).send({ error: 'too_many_attempts' });
    const { password } = z.object({ password: z.string().max(200) }).parse(req.body);
    const stored = repo.getFamilyPasswordHash();
    if (!stored || !(await verifyPassword(password, stored))) {
      limiter.fail(key);
      return reply.code(401).send({ error: 'invalid_password' });
    }
    const token = newToken();
    repo.addParentSession(hashToken(token));
    return { token };
  });

  app.register(async (p) => {
    p.addHook('preHandler', requireParent);

    const view = (id: string) => {
      const d = repo.getDevice(id);
      return d ? { ...repo.toView(d), online: hub.isOnline(id) } : null;
    };
    const IdParam = z.object({ id: z.string().uuid() });

    p.get('/api/devices', async () => repo.listDevices().map((d) => ({ ...repo.toView(d), online: hub.isOnline(d.id) })));

    p.get('/api/devices/:id', async (req, reply) => {
      const v = view(IdParam.parse(req.params).id);
      return v ?? reply.code(404).send({ error: 'not_found' });
    });

    /** 자녀 기기 페어링 코드 발급 (6자리, 10분 유효, 1회용) */
    p.post('/api/devices/pairing-codes', async (req) => {
      const { name, platform } = z.object({ name: z.string().min(1).max(64), platform: PlatformSchema }).parse(req.body);
      const code = newPairingCode();
      const expiresAt = Date.now() + config.pairingCodeTtlMinutes * 60_000;
      repo.createPairingCode(code, name, platform, expiresAt);
      return { code, expiresAt };
    });

    p.delete('/api/devices/:id', async (req, reply) => {
      const { id } = IdParam.parse(req.params);
      hub.disconnectAgent(id);
      return repo.deleteDevice(id) ? { ok: true } : reply.code(404).send({ error: 'not_found' });
    });

    p.get('/api/devices/:id/policy', async (req, reply) => {
      const policy = repo.getPolicy(IdParam.parse(req.params).id);
      return policy ?? reply.code(404).send({ error: 'not_found' });
    });

    /** 허용 시간·취침 시간·차단 앱 목록 수정 (부분 수정 가능) */
    p.put('/api/devices/:id/policy', async (req, reply) => {
      const { id } = IdParam.parse(req.params);
      const patch = PolicyUpdateSchema.parse(req.body);
      const next = repo.updatePolicy(id, (cur) => ({ ...cur, ...patch }));
      if (!next) return reply.code(404).send({ error: 'not_found' });
      await hub.pushPolicy(id);
      return next;
    });

    /** 원격 즉시 잠금/해제 */
    p.post('/api/devices/:id/lock', async (req, reply) => {
      const { id } = IdParam.parse(req.params);
      const { locked } = z.object({ locked: z.boolean() }).parse(req.body);
      const next = repo.updatePolicy(id, (cur) => ({ ...cur, manualLock: locked }));
      if (!next) return reply.code(404).send({ error: 'not_found' });
      await hub.pushPolicy(id);
      return next;
    });

    /** 오늘만 추가 시간 부여 (같은 날 여러 번 누르면 누적) */
    p.post('/api/devices/:id/extra-time', async (req, reply) => {
      const { id } = IdParam.parse(req.params);
      const { minutes } = z.object({ minutes: z.number().int().min(-720).max(720) }).parse(req.body);
      const next = repo.updatePolicy(id, (cur) => {
        const today = todayIn(cur.timezone);
        const base = cur.extraTime?.date === today ? cur.extraTime.minutes : 0;
        return { ...cur, extraTime: { date: today, minutes: Math.max(0, Math.min(720, base + minutes)) } };
      });
      if (!next) return reply.code(404).send({ error: 'not_found' });
      await hub.pushPolicy(id);
      return next;
    });

    /** 학습 모드 즉시 시작(minutes > 0) / 종료(0). 시간표와 별개로 동작 */
    p.post('/api/devices/:id/study', async (req, reply) => {
      const { id } = IdParam.parse(req.params);
      const { minutes } = z.object({ minutes: z.number().int().min(0).max(480) }).parse(req.body);
      const next = repo.updatePolicy(id, (cur) => ({
        ...cur,
        studySession: minutes === 0 ? null : { until: new Date(Date.now() + minutes * 60_000).toISOString(), by: 'parent' },
      }));
      if (!next) return reply.code(404).send({ error: 'not_found' });
      await hub.pushPolicy(id);
      return next;
    });

    /** 기기가 보고한 앱 목록 (Android: 설치된 앱 / Windows: 최근 실행된 프로그램) */
    p.get('/api/devices/:id/apps', async (req) => repo.getApps(IdParam.parse(req.params).id));

    // ----- 아이의 허용 요청 -----
    p.get('/api/requests', async (req) => {
      const { status } = z.object({ status: z.enum(['pending', 'all']).default('pending') }).parse(req.query);
      return repo.listRequests(status).map((r) => ({
        id: r.id, deviceId: r.device_id, deviceName: r.device_name, kind: r.kind, value: r.value,
        note: r.note, status: r.status, createdAt: r.created_at, decidedAt: r.decided_at,
      }));
    });

    p.post('/api/requests/:rid', async (req, reply) => {
      const { rid } = z.object({ rid: z.coerce.number().int().positive() }).parse(req.params);
      const { decision } = z.object({ decision: z.enum(['today', 'always', 'deny']) }).parse(req.body);
      const r = repo.getRequest(rid);
      if (!r) return reply.code(404).send({ error: 'not_found' });
      // 엄마·아빠가 동시에 눌러도 한 번만 반영
      if (!repo.decideRequest(rid, decision)) return reply.code(409).send({ error: 'already_decided' });
      if (decision !== 'deny') {
        repo.updatePolicy(r.device_id, (cur) => applyDecision(cur, r, decision, todayIn(cur.timezone)));
        await hub.pushPolicy(r.device_id);
      }
      hub.sendToDevice(r.device_id, { type: 'request_result', requestId: r.id, kind: r.kind, value: r.value, decision });
      hub.broadcastParents({ type: 'requests_changed' });
      return { ok: true };
    });

    p.get('/api/devices/:id/events', async (req) => {
      const { id } = IdParam.parse(req.params);
      const { limit } = z.object({ limit: z.coerce.number().int().min(1).max(500).default(50) }).parse(req.query);
      return repo.listEvents(id, limit);
    });

    p.get('/api/devices/:id/usage', async (req) => {
      const { id } = IdParam.parse(req.params);
      const { days } = z.object({ days: z.coerce.number().int().min(1).max(90).default(7) }).parse(req.query);
      return repo.getUsageHistory(id, days);
    });

    // ----- 웹 푸시 구독 -----
    p.get('/api/push/vapid-public-key', async () => ({ key: config.vapid.publicKey || null }));
    p.post('/api/push/subscribe', async (req) => {
      const sub = z
        .object({ endpoint: z.string().url(), keys: z.object({ p256dh: z.string(), auth: z.string() }) })
        .passthrough()
        .parse(req.body);
      repo.savePushSubscription(sub.endpoint, JSON.stringify(sub));
      return { ok: true };
    });
  });

  /** 부모앱 실시간 갱신. 브라우저 WebSocket 은 헤더를 못 붙여서 쿼리 토큰 사용 (HTTPS 전제) */
  app.get('/ws/parent', { websocket: true }, (socket, req) => {
    const token = (req.query as { token?: string }).token;
    if (!token || !repo.hasParentSession(hashToken(token))) {
      socket.close(4401, 'unauthorized');
      return;
    }
    hub.attachParent(socket);
  });
}
