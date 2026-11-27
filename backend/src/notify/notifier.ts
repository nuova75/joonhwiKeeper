/**
 * 외부 알림 채널 (모두 무료):
 *  - FCM: 절전(Doze) 중인 Android 에이전트를 깨워 WebSocket 재접속·정책 동기화
 *  - Web Push(VAPID): 부모 PWA 알림 (iOS 는 홈 화면에 추가한 PWA 에서만 수신)
 * 설정이 없으면 조용히 비활성화되어, 로컬 개발 시 아무 키 없이도 서버가 뜬다.
 */
import webpush from 'web-push';
import { readFileSync } from 'node:fs';
import type { FastifyBaseLogger } from 'fastify';
import type { Config } from '../config.js';
import type { Repo } from '../db.js';

export interface Notifier {
  wakeAgent(fcmToken: string | null): Promise<void>;
  notifyParents(title: string, body: string): Promise<void>;
}

type FcmSend = (msg: { token: string; data: Record<string, string>; android: { priority: 'high' } }) => Promise<string>;

export async function createNotifier(config: Config, repo: Repo, log: FastifyBaseLogger): Promise<Notifier> {
  const pushEnabled = !!(config.vapid.publicKey && config.vapid.privateKey);
  if (pushEnabled) {
    webpush.setVapidDetails(config.vapid.subject, config.vapid.publicKey, config.vapid.privateKey);
  } else {
    log.warn('VAPID 키 없음 → 부모 웹 푸시 비활성 (npm run gen:vapid)');
  }

  let fcmSend: FcmSend | null = null;
  if (config.fcmServiceAccountPath) {
    try {
      // firebase-admin 은 optionalDependency: 설치돼 있을 때만 로드
      const admin = await import('firebase-admin');
      const app = admin.default.initializeApp({
        credential: admin.default.credential.cert(JSON.parse(readFileSync(config.fcmServiceAccountPath, 'utf8'))),
      });
      const messaging = app.messaging();
      fcmSend = (msg) => messaging.send(msg);
      log.info('FCM 활성화');
    } catch (err) {
      log.error({ err }, 'FCM 초기화 실패 → 비활성');
    }
  } else {
    log.warn('FCM_SERVICE_ACCOUNT 없음 → Android 깨우기 비활성 (WebSocket 재접속에만 의존)');
  }

  return {
    async wakeAgent(fcmToken) {
      if (!fcmSend || !fcmToken) return;
      try {
        // data 메시지 + high priority: 앱이 백그라운드여도 onMessageReceived 가 호출됨
        await fcmSend({ token: fcmToken, data: { type: 'sync' }, android: { priority: 'high' } });
      } catch (err) {
        log.warn({ err }, 'FCM 발송 실패');
      }
    },
    async notifyParents(title, body) {
      if (!pushEnabled) return;
      const payload = JSON.stringify({ title, body });
      await Promise.all(
        repo.listPushSubscriptions().map(async (s) => {
          try {
            await webpush.sendNotification(JSON.parse(s.json), payload);
          } catch (err) {
            const status = (err as { statusCode?: number }).statusCode;
            if (status === 404 || status === 410) repo.deletePushSubscription(s.endpoint); // 만료된 구독 정리
            else log.warn({ err }, '웹 푸시 실패');
          }
        }),
      );
    },
  };
}

export const nullNotifier: Notifier = {
  async wakeAgent() {},
  async notifyParents() {},
};
