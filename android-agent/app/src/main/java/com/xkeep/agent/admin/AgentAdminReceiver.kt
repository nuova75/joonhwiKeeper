package com.xkeep.agent.admin

import android.app.admin.DeviceAdminReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.util.Log
import com.xkeep.agent.XKeepApp
import com.xkeep.agent.service.EnforcementService

/**
 * Device Owner 진입점.
 * `adb shell dpm set-device-owner com.xkeep.agent/.admin.AgentAdminReceiver` 실행 시
 * onEnabled 가 호출되고, 이때 기본 보호 정책(삭제 차단·안전 모드 차단 등)을 즉시 적용한다.
 */
class AgentAdminReceiver : DeviceAdminReceiver() {

    override fun onEnabled(context: Context, intent: Intent) {
        Log.i(TAG, "Device admin enabled")
        applyBaseline(context)
    }

    /** QR/NFC 프로비저닝(공장 초기화 후 설정 마법사에서 등록)으로 들어온 경우 */
    override fun onProfileProvisioningComplete(context: Context, intent: Intent) {
        applyBaseline(context)
    }

    override fun onDisableRequested(context: Context, intent: Intent): CharSequence =
        "보호 기능을 해제하면 부모님께 알림이 전송됩니다."

    private fun applyBaseline(context: Context) {
        val pm = XKeepApp.from(context).policyManager
        if (pm.isDeviceOwner) {
            runCatching { pm.applyBaseline() }.onFailure { Log.e(TAG, "baseline 적용 실패", it) }
            EnforcementService.start(context)
        }
    }

    companion object {
        private const val TAG = "AgentAdminReceiver"
        fun componentName(context: Context) = ComponentName(context, AgentAdminReceiver::class.java)
    }
}
