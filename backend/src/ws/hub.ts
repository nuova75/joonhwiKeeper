/**
 * 실시간 연결 허브.
 *  - 에이전트 소켓: deviceId → WebSocket (기기당 1개, 재접속 시 이전 소켓 교체)
 *  - 부모 소켓: 상태 변경 알림 브로드캐스트 (부모앱은 알림을 받으면 REST 로 다시 조회)
 */
import type { WebSocket } from 'ws';
import type { FastifyBaseLogger } from 'fastify';
import type { Repo } from '../db.js';
import type { Notifier } from '../notify/notifier.js';
import type { ServerToAgentMessage, ServerToParentMessage } from '../protocol.js';

export class Hub {
  private readonly agents = new Map<string, WebSocket>();
  private readonly parents = new Set<WebSocket>();

  constructor(
    private readonly repo: Repo,
    private readonly notifier: Notifier,
    private readonly log: FastifyBaseLogger,
  ) {}

  attachAgent(deviceId: string, ws: WebSocket): void {
    const prev = this.agents.get(deviceId);
    if (prev && prev !== ws) prev.close(4000, 'replaced');
    this.agents.set(deviceId, ws);
    ws.on('close', () => {
      if (this.agents.get(deviceId) === ws) {
        this.agents.delete(deviceId);
        this.notifyParentsStatus(deviceId);
      }
    });
    this.notifyParentsStatus(deviceId);
  }

  isOnline(deviceId: string): boolean {
    return this.agents.has(deviceId);
  }

  disconnectAgent(deviceId: string): void {
    this.agents.get(deviceId)?.close(4001, 'device removed');
  }

  /** 최신 정책 전달: 연결돼 있으면 즉시 WS 로, 아니면 FCM 으로 깨워서 재접속 유도 */
  async pushPolicy(deviceId: string): Promise<void> {
    const device = this.repo.getDevice(deviceId);
    const policy = this.repo.getPolicy(deviceId);
    if (!device || !policy) return;
    const ws = this.agents.get(deviceId);
    if (ws && ws.readyState === ws.OPEN) {
      this.sendAgent(ws, { type: 'policy', policy });
    } else {
      this.log.info({ deviceId }, '에이전트 오프라인 → FCM 깨우기 시도');
      await this.notifier.wakeAgent(device.fcm_token);
    }
    this.notifyParentsStatus(deviceId);
  }

  /** 연결돼 있을 때만 전달 (오프라인이면 버림: 정책은 재접속 시 따로 동기화됨) */
  sendToDevice(deviceId: string, msg: ServerToAgentMessage): void {
    const ws = this.agents.get(deviceId);
    if (ws && ws.readyState === ws.OPEN) this.sendAgent(ws, msg);
  }

  sendAgent(ws: WebSocket, msg: ServerToAgentMessage): void {
    ws.send(JSON.stringify(msg));
  }

  attachParent(ws: WebSocket): void {
    this.parents.add(ws);
    ws.on('close', () => this.parents.delete(ws));
  }

  notifyParentsStatus(deviceId: string): void {
    this.broadcastParents({ type: 'device_status', deviceId });
  }

  broadcastParents(msg: ServerToParentMessage): void {
    const data = JSON.stringify(msg);
    for (const ws of this.parents) if (ws.readyState === ws.OPEN) ws.send(data);
  }
}
