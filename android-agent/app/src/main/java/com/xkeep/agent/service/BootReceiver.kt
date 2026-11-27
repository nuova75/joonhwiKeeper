package com.xkeep.agent.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.xkeep.agent.XKeepApp

/** 부팅 완료·앱 업데이트 후 집행 서비스 재시작 + 기본 정책 재적용 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val app = XKeepApp.from(context)
        if (app.policyManager.isDeviceOwner) runCatching { app.policyManager.applyBaseline() }
        if (app.config.isPaired) EnforcementService.start(context)
    }
}
