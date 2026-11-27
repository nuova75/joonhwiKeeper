package com.xkeep.agent.fcm

import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import com.xkeep.agent.XKeepApp
import com.xkeep.agent.service.EnforcementService
import kotlinx.coroutines.launch

/**
 * FCM 은 "깨우기 신호"로만 쓴다 (정책 본문은 항상 WebSocket 으로 받음).
 * 서버가 오프라인 기기에 정책을 보낼 때 data {type: sync} 를 high priority 로 발송하면,
 * Doze 상태여도 잠시 네트워크가 허용되어 서비스가 WebSocket 에 재접속한다.
 */
class AgentMessagingService : FirebaseMessagingService() {

    override fun onMessageReceived(message: RemoteMessage) {
        if (message.data["type"] == "sync") {
            EnforcementService.start(this, EnforcementService.ACTION_SYNC)
        }
    }

    override fun onNewToken(token: String) {
        val app = XKeepApp.from(this)
        if (app.config.isPaired) app.appScope.launch { app.api.updateFcmToken(token) }
    }
}
