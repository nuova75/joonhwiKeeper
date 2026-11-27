/**
 * 변조 감지: 정상 종료 신호(goodbye) 없이 일정 시간 이상 연결이 끊긴 기기는
 * 에이전트 강제 종료·네트워크 차단·초기화 등 우회 시도일 수 있으므로 부모에게 알린다.
 */
import type { Config } from './config.js';
import type { Repo } from './db.js';
import type { Notifier } from './notify/notifier.js';
import type { Hub } from './ws/hub.js';
import { PolicySchema } from './protocol.js';

function localHHMM(timezone: string, now: Date): string {
  return new Intl.DateTimeFormat('en-GB', { timeZone: timezone, hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }).format(now);
}
function localDate(timezone: string, now: Date): string {
  return new Intl.DateTimeFormat('en-CA', { timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
}

/**
 * 하루 요약: 실시간 "딴짓 적발" 알림 대신 저녁에 한 번만 담담하게 알린다.
 * 기기 시간대 기준 summaryTime 이 지났고 오늘 아직 안 보냈으면 전송.
 */
export function sendDailySummaries(deps: { repo: Repo; notifier: Notifier; config: Config }, now = new Date()): number {
  const { repo, notifier, config } = deps;
  if (!config.summaryTime) return 0;
  let sent = 0;
  for (const d of repo.listDevices()) {
    const tz = PolicySchema.parse(JSON.parse(d.policy_json)).timezone;
    const today = localDate(tz, now);
    if (d.summary_sent_date === today || localHHMM(tz, now) < config.summaryTime) continue;
    repo.setSummarySent(d.id, today);
    const day = repo.getUsageHistory(d.id, 1).find((u) => u.date === today);
    if (!day) continue; // 오늘 사용 기록 없음
    const free = Math.max(0, day.minutes - day.studyMinutes);
    const pending = repo.listRequests('pending').filter((r) => r.device_id === d.id).length;
    const parts = [`학습 ${day.studyMinutes}분`, `자유 ${free}분`];
    if (day.blocked) parts.push(`차단 ${day.blocked}회`);
    if (pending) parts.push(`대기 중인 요청 ${pending}건`);
    void notifier.notifyParents(`${d.name} 오늘 요약`, parts.join(' · '));
    sent++;
  }
  return sent;
}

export function startWatchdog(deps: { repo: Repo; hub: Hub; notifier: Notifier; config: Config }): NodeJS.Timeout {
  const { repo, hub, notifier, config } = deps;
  return setInterval(() => {
    const threshold = Date.now() - config.offlineAlertMinutes * 60_000;
    for (const d of repo.listDevices()) {
      if (hub.isOnline(d.id) || d.graceful_offline || d.offline_alerted) continue;
      if (d.last_seen !== null && d.last_seen < threshold) {
        repo.markOfflineAlerted(d.id);
        void notifier.notifyParents(
          `${d.name}: 연결 끊김`,
          `${config.offlineAlertMinutes}분 이상 정상 종료 신호 없이 연결이 끊겼습니다. 네트워크 차단이나 에이전트 종료 여부를 확인하세요.`,
        );
      }
    }
    sendDailySummaries({ repo, notifier, config });
  }, 60_000);
}
