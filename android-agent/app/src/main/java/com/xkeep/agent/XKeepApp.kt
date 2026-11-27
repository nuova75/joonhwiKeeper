package com.xkeep.agent

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import com.xkeep.agent.net.AgentConfig
import com.xkeep.agent.net.AgentSocket
import com.xkeep.agent.net.ApiClient
import com.xkeep.agent.policy.LockReason
import com.xkeep.agent.policy.PolicyManager
import com.xkeep.agent.policy.PolicyStore
import com.xkeep.agent.service.EnforcementService
import com.xkeep.agent.usage.StudyCounter
import com.xkeep.agent.usage.UsageTracker
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/** UI 와 잠금 화면이 구독하는 현재 판정 상태 */
data class EnforcementState(
    val lockReason: LockReason? = null,
    /** 자유 시간 사용(분) = 오늘 전체 - 학습 */
    val usedMinutes: Int = 0,
    val studyMinutes: Int = 0,
    val remainingMinutes: Int? = null,
    /** 학습 모드면 끝나는 시각 "HH:mm", 아니면 null */
    val studyUntil: String? = null,
    val usageAccess: Boolean = true,
    val deviceOwner: Boolean = false,
)

/** 간단한 수동 DI 컨테이너 (MVP 규모에서는 Hilt 불필요) */
class XKeepApp : Application() {
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    val http: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .pingInterval(30, TimeUnit.SECONDS) // NAT/프록시 유휴 끊김 방지 + 끊김 빠른 감지
            .connectTimeout(15, TimeUnit.SECONDS)
            .build()
    }
    val config by lazy { AgentConfig(this) }
    val policyStore by lazy { PolicyStore(this) }
    val policyManager by lazy { PolicyManager(this) }
    val usageTracker by lazy { UsageTracker(this) }
    val studyCounter by lazy { StudyCounter(this) }
    val api by lazy { ApiClient(http, config) }
    val socket by lazy { AgentSocket(http, config, policyStore, appScope) }
    val state = MutableStateFlow(EnforcementState())

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_STATUS, getString(R.string.channel_status), NotificationManager.IMPORTANCE_LOW),
        )
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_LOCK, getString(R.string.channel_lock), NotificationManager.IMPORTANCE_HIGH),
        )
        if (config.isPaired) EnforcementService.start(this)
    }

    companion object {
        const val CHANNEL_STATUS = "status"
        const val CHANNEL_LOCK = "lock"
        fun from(context: Context) = context.applicationContext as XKeepApp
    }
}
