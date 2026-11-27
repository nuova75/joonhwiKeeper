package com.xkeep.agent.service

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.xkeep.agent.XKeepApp
import com.xkeep.agent.lock.LockActivity
import com.xkeep.agent.net.AppInfo
import com.xkeep.agent.net.Apps
import com.xkeep.agent.net.Event
import com.xkeep.agent.net.RequestResult
import com.xkeep.agent.net.Goodbye
import com.xkeep.agent.net.Usage
import com.xkeep.agent.policy.LimitEvaluator
import com.xkeep.agent.policy.LockReason
import com.xkeep.agent.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.OffsetDateTime
import java.time.ZonedDateTime

/**
 * 1분 주기 집행 루프 (포그라운드 서비스).
 *
 *   사용 시간 집계 → LimitEvaluator 판정 → PolicyManager 로 정지 상태 동기화 → 잠금 화면 → 서버 보고
 *
 * 정책이 바뀌면(서버 push) 1분을 기다리지 않고 즉시 한 번 더 실행한다.
 * 잠금의 실제 강제력은 "앱 정지(suspend)"이고, LockActivity 는 안내 화면 + 키오스크 모드다.
 * 즉 잠금 화면이 어떤 이유로 안 떠도 앱은 이미 실행 불가 상태다.
 */
class EnforcementService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val tickMutex = Mutex()
    private val app get() = XKeepApp.from(this)
    private var lastReason: LockReason? = null
    private var lastTamper = false
    private var lastOwner = false
    private var lastStudy = false
    @Volatile private var appsSentAt = 0L

    private val systemReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            when (intent.action) {
                // 정상 전원 종료 → 서버에 알려 "연결 끊김" 경고를 억제
                Intent.ACTION_SHUTDOWN -> app.socket.send(Goodbye("shutdown"))
                // 화면을 켜는 순간 바로 판정 (슬립 중엔 1분 타이머가 멈춰 있을 수 있음)
                Intent.ACTION_SCREEN_ON, Intent.ACTION_USER_PRESENT -> scope.launch { tick() }
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        ServiceCompat.startForeground(
            this, NOTIF_ID, buildNotification("보호 중"),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE,
        )
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_SHUTDOWN)
            addAction(Intent.ACTION_SCREEN_ON)
            addAction(Intent.ACTION_USER_PRESENT)
        }
        // 시스템 보호 브로드캐스트만 수신하므로 NOT_EXPORTED 로 충분
        ContextCompat.registerReceiver(this, systemReceiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        app.socket.start()

        scope.launch {
            while (isActive) {
                tick()
                delay(TICK_MS)
            }
        }
        // 정책 변경 즉시 반영
        scope.launch {
            app.policyStore.policy.collect { tick() } // StateFlow 는 이미 중복 값을 걸러냄
        }
        // 재접속 직후 현재 상태 보고
        scope.launch {
            app.socket.connected.collect { if (it) { appsSentAt = 0L; tick() } }
        }
        // 요청 처리 결과를 아이에게 알림으로
        scope.launch {
            app.socket.results.collect { notifyRequestResult(it) }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_SYNC -> app.socket.connectNow()      // FCM 깨우기
            ACTION_TICK -> scope.launch { tick() }
        }
        return START_STICKY
    }

    private suspend fun tick() {
        tickMutex.withLock { tickLocked() }
    }

    private fun tickLocked() {
        val policy = app.policyStore.policy.value ?: return
        val zone = LimitEvaluator.zoneOf(policy)
        val now = ZonedDateTime.now(zone)
        val today = now.toLocalDate().toString()
        val pm = app.policyManager
        val isOwner = pm.isDeviceOwner
        val hasAccess = app.usageTracker.hasUsageAccess()

        val snapshot = if (hasAccess) runCatching { app.usageTracker.today(zone) }.getOrNull() else null
        val total = snapshot?.usedMinutes ?: 0
        // 사용 기록 권한이 꺼지면 시간을 잴 수 없으므로 fail-closed: 잠근다
        val tamper = !hasAccess

        // 학습 여부는 사용량과 무관하게 정해지므로 먼저 판정 → 학습 시간 누적 → 자유 시간으로 최종 판정
        val interactive = getSystemService(PowerManager::class.java).isInteractive
        val studyNow = LimitEvaluator.decide(policy, 0, now, tamper).study
        val studyMinutes = app.studyCounter.record(today, studyNow && interactive).coerceAtMost(total)
        val free = (total - studyMinutes).coerceAtLeast(0)
        val decision = LimitEvaluator.decide(policy, free, now, tamper)
        val reason = decision.lock
        val remaining = LimitEvaluator.remainingMinutes(policy, free, now)
        val studyUntil = if (decision.study) LimitEvaluator.studyEndsAt(policy, now)?.toLocalTime()?.toString() else null

        if (isOwner) {
            runCatching {
                pm.applyEnforcement(
                    blocked = policy.blockedApps.toSet(),
                    lockAll = reason != null,
                    alwaysAllowed = policy.alwaysAllowed.toSet(),
                    studyAllowed = if (decision.study) LimitEvaluator.studyApps(policy, now) else null,
                )
                pm.applyBrowserPolicy(if (decision.study) LimitEvaluator.studySites(policy, now) else null)
            }.onFailure { Log.e(TAG, "enforcement failed", it) }
        }

        app.state.value = app.state.value.copy(
            lockReason = reason, usedMinutes = free, studyMinutes = studyMinutes, remainingMinutes = remaining,
            studyUntil = studyUntil, usageAccess = hasAccess, deviceOwner = isOwner,
        )

        // 상태 변화 이벤트
        if (tamper && !lastTamper) report("usage_access_revoked", "사용 기록 접근 권한이 꺼짐")
        if (reason != null && lastReason == null) report("lock_started", reason.name.lowercase())
        if (reason == null && lastReason != null) report("lock_ended", "")
        if (decision.study && !lastStudy) report("study_started", studyUntil?.let { "~$it" }.orEmpty())
        if (!decision.study && lastStudy) report("study_ended", "")
        lastTamper = tamper
        lastReason = reason
        lastStudy = decision.study

        if (reason != null) showLock()
        updateNotification(reason, free, remaining, studyUntil)

        app.socket.send(
            Usage(
                date = today,
                usedMinutes = total.coerceAtMost(1440),
                perApp = snapshot?.perAppMinutes.orEmpty(),
                locked = reason != null,
                lockReason = reason,
                studyMinutes = studyMinutes,
                mode = decision.mode,
            ),
        )
        sendAppsIfDue()
        if (isOwner != lastOwner && lastOwner) report("device_owner_lost", "Device Owner 권한이 해제됨")
        lastOwner = isOwner
    }

    /** 설치 앱 목록은 접속 직후와 30분마다 보고 (부모앱의 "앱 목록에서 고르기"용) */
    private fun sendAppsIfDue() {
        val nowMs = System.currentTimeMillis()
        if (nowMs - appsSentAt < APPS_INTERVAL_MS) return
        val apps = runCatching { app.policyManager.launchableApps() }.getOrNull() ?: return
        if (app.socket.send(Apps(apps.map { (id, label) -> AppInfo(id, label) }))) appsSentAt = nowMs
    }

    private fun notifyRequestResult(r: RequestResult) {
        val what = if (r.kind == "site") "사이트" else "앱"
        val text = when (r.decision) {
            "today" -> "오늘은 쓸 수 있어요."
            "always" -> "이제 학습 시간에 항상 쓸 수 있어요."
            "deny" -> "부모님이 허락하지 않았어요."
            else -> "주소 형식을 알아볼 수 없어요. 예: ebsi.co.kr"
        }
        val n = NotificationCompat.Builder(this, XKeepApp.CHANNEL_LOCK)
            .setSmallIcon(android.R.drawable.ic_dialog_info)
            .setContentTitle("$what 요청: ${r.value}")
            .setContentText(text)
            .setAutoCancel(true)
            .build()
        runCatching { getSystemService(android.app.NotificationManager::class.java).notify(RESULT_NOTIF_ID, n) }
    }

    private fun report(kind: String, detail: String) {
        app.socket.send(Event(kind, detail, OffsetDateTime.now().toString()))
    }

    /**
     * 잠금 화면 띄우기.
     * Device Owner 앱은 Android 10+ 백그라운드 액티비티 시작 제한의 예외 대상이지만,
     * 혹시 막히는 기기를 위해 full-screen intent 알림을 함께 보낸다.
     */
    private fun showLock() {
        val intent = Intent(this, LockActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        runCatching { startActivity(intent) }.onFailure { Log.w(TAG, "startActivity blocked", it) }
        val pi = PendingIntent.getActivity(this, 1, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = NotificationCompat.Builder(this, XKeepApp.CHANNEL_LOCK)
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentTitle("사용이 잠겼어요")
            .setCategory(NotificationCompat.CATEGORY_ALARM)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setFullScreenIntent(pi, true)
            .setOngoing(true)
            .build()
        runCatching { getSystemService(android.app.NotificationManager::class.java).notify(LOCK_NOTIF_ID, n) }
    }

    private fun updateNotification(reason: LockReason?, used: Int, remaining: Int?, studyUntil: String?) {
        val text = when {
            reason != null -> "잠김 · ${reason.label}"
            studyUntil != null -> "📚 학습 시간 · ${studyUntil}까지 (학습 앱·사이트만 열려요)"
            remaining != null -> "자유 시간 ${used}분 사용 · ${remaining}분 남음"
            else -> "자유 시간 ${used}분 사용"
        }
        val nm = getSystemService(android.app.NotificationManager::class.java)
        nm.notify(NOTIF_ID, buildNotification(text))
        if (reason == null) nm.cancel(LOCK_NOTIF_ID)
    }

    private fun buildNotification(text: String): Notification {
        val pi = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, XKeepApp.CHANNEL_STATUS)
            .setSmallIcon(android.R.drawable.ic_lock_lock)
            .setContentTitle("xKeep")
            .setContentText(text)
            .setContentIntent(pi)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()
    }

    override fun onDestroy() {
        runCatching { unregisterReceiver(systemReceiver) }
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        private const val TAG = "EnforcementService"
        private const val NOTIF_ID = 1
        private const val LOCK_NOTIF_ID = 2
        private const val RESULT_NOTIF_ID = 3
        private const val APPS_INTERVAL_MS = 30 * 60_000L
        private const val TICK_MS = 60_000L
        const val ACTION_SYNC = "com.xkeep.agent.SYNC"
        const val ACTION_TICK = "com.xkeep.agent.TICK"

        fun start(context: Context, action: String? = null) {
            val intent = Intent(context, EnforcementService::class.java).setAction(action)
            // Device Owner 앱은 Android 12+ 백그라운드 FGS 시작 제한의 예외 대상.
            // DO 설정 전(개발 중)에는 실패할 수 있으므로 앱이 죽지 않게 감싼다.
            runCatching { context.startForegroundService(intent) }
                .onFailure { Log.w(TAG, "startForegroundService failed", it) }
        }
    }
}
