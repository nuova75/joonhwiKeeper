/**
 * 자녀 기기 에이전트용 API.
 *  - POST /api/agent/pair      : 페어링 코드로 기기 등록 → deviceId + 기기 토큰 발급
 *  - POST /api/agent/fcm-token : FCM 토큰 갱신 (Android)
 *  - GET  /ws/agent            : Authorization: Bearer <기기 토큰> 로 WebSocket 연결
 */
import type { FastifyInstance, FastifyRequest } from 'fastify';
import { z } from 'zod';
import { bearer, hashToken, newToken } from '../auth.js';
import type { DeviceRow, Repo } from '../db.js';
import { AgentMessageSchema, normalizeSite, PairRequestSchema } from '../protocol.js';
import type { Hub } from '../ws/hub.js';
import type { Notifier } from '../notify/notifier.js';

/**
 * 부모에게 즉시 푸시할 이벤트는 "보호 장치가 깨졌을 수 있는" 경우만.
 * 차단 시도(process_killed 등)는 시스템이 이미 막았으므로 실시간 알림 대신 하루 요약에 포함한다.
 * (실시간 "딴짓 적발" 알림이 부모-아이 갈등의 원인이 되기 때문)
 */
const EVENT_LABELS: Record<string, string> = {
  usage_access_revoked: '⚠️ 사용 기록 권한 해제됨 (우회 시도 의심)',
  device_owner_lost: '⚠️ 관리자 권한 상실 (우회 시도 의심)',
  tamper_suspected: '⚠️ 보호 프로그램 우회 시도 의심',
};

export async function agentRoutes(app: FastifyInstance, deps: { repo: Repo; hub: Hub; notifier: Notifier }) {
  const { repo, hub, notifier } = deps;

  const authDevice = (req: FastifyRequest): DeviceRow | null => {
    const token = bearer(req.headers.authorization);
    return token ? repo.getDeviceByTokenHash(hashToken(token)) : null;
  };

  app.post('/api/agent/pair', async (req, reply) => {
    const body = PairRequestSchema.parse(req.body);
    const pairing = repo.consumePairingCode(body.code);
    if (!pairing) return reply.code(400).send({ error: 'invalid_or_expired_code' });
    if (pairing.platform !== body.platform) return reply.code(400).send({ error: 'platform_mismatch' });

    const token = newToken();
    const device = repo.createDevice(pairing.name, pairing.platform, hashToken(token), body.fcmToken ?? null);
    hub.notifyParentsStatus(device.id);
    req.log.info({ deviceId: device.id, platform: device.platform }, '기기 페어링 완료');
    return { deviceId: device.id, token, policy: repo.getPolicy(device.id) };
  });

  app.post('/api/agent/fcm-token', async (req, reply) => {
    const device = authDevice(req);
    if (!device) return reply.code(401).send({ error: 'unauthorized' });
    const { fcmToken } = z.object({ fcmToken: z.string().min(1).max(4096) }).parse(req.body);
    repo.setFcmToken(device.id, fcmToken);
    return { ok: true };
  });

  app.get('/ws/agent', { websocket: true }, (socket, req) => {
    const device = authDevice(req);
    if (!device) {
      socket.close(4401, 'unauthorized');
      return;
    }
    const deviceId = device.id;
    repo.markSeen(deviceId);
    hub.attachAgent(deviceId, socket);

    socket.on('message', (raw) => {
      let msg;
      try {
        msg = AgentMessageSchema.parse(JSON.parse(raw.toString()));
      } catch (err) {
        req.log.warn({ deviceId, err }, '잘못된 에이전트 메시지');
        return;
      }
      repo.markSeen(deviceId);

      switch (msg.type) {
        case 'hello': {
          // 접속 시 항상 최신 정책 전송 (에이전트가 가진 버전과 다르면 덮어씀)
          const policy = repo.getPolicy(deviceId);
          if (policy && policy.version !== msg.policyVersion) hub.sendAgent(socket, { type: 'policy', policy });
          break;
        }
        case 'usage':
          repo.saveUsage(deviceId, msg, msg);
          hub.notifyParentsStatus(deviceId);
          break;
        case 'apps':
          repo.saveApps(deviceId, msg.apps);
          break;
        case 'access_request': {
          // 사이트 요청은 주소 전체가 와도 도메인 단위로 허용 (경로 단위는 부모가 설정에서 직접)
          const value = msg.kind === 'site' ? normalizeSite(msg.value)?.split('/')[0] : msg.value.trim();
          if (!value) {
            hub.sendAgent(socket, { type: 'request_result', requestId: null, kind: msg.kind, value: msg.value, decision: 'invalid' });
            break;
          }
          const created = repo.addRequest(deviceId, msg.kind, value, msg.note);
          if (created) {
            hub.broadcastParents({ type: 'requests_changed' });
            const what = msg.kind === 'site' ? '사이트' : '앱';
            void notifier.notifyParents(`${device.name}: ${what} 허용 요청`, `${value}${msg.note ? ` — ${msg.note}` : ''}`);
          }
          break;
        }
        case 'study_start': {
          // 아이가 스스로 시작: 진행 중인 세션보다 늦게 끝나는 경우에만 연장
          const until = new Date(Date.now() + msg.minutes * 60_000);
          repo.updatePolicy(deviceId, (cur) => {
            const curUntil = cur.studySession ? new Date(cur.studySession.until) : null;
            if (curUntil && curUntil >= until) return cur;
            return { ...cur, studySession: { until: until.toISOString(), by: cur.studySession?.by ?? 'child' } };
          });
          void hub.pushPolicy(deviceId);
          break;
        }
        case 'processes':
          repo.saveProcesses(deviceId, msg.names);
          break;
        case 'event': {
          repo.addEvent(deviceId, msg.kind, msg.detail, msg.at);
          hub.broadcastParents({ type: 'device_event', deviceId, kind: msg.kind, detail: msg.detail, at: msg.at });
          const label = EVENT_LABELS[msg.kind];
          if (label) void notifier.notifyParents(`${device.name}: ${label}`, msg.detail);
          break;
        }
        case 'goodbye':
          // 정상 종료(PC 끄기/폰 전원 끄기) 신호 → 오프라인 경고 대상에서 제외
          repo.markGracefulOffline(deviceId);
          break;
      }
    });
  });
}
