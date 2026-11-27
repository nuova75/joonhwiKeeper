package com.xkeep.agent.usage

import android.app.AppOpsManager
import android.app.usage.UsageEvents
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Process
import java.time.Instant
import java.time.ZoneId

data class UsageSnapshot(
    val date: String,
    val usedMinutes: Int,
    /** 패키지별 분 (1분 미만 앱은 제외) */
    val perAppMinutes: Map<String, Int>,
    val foregroundPackage: String?,
)

/**
 * UsageStatsManager 이벤트로 "오늘" 포그라운드 사용 시간을 계산한다.
 *
 * queryUsageStats(INTERVAL_DAILY) 집계값은 버킷 경계가 기기 시간대와 어긋나거나
 * 갱신이 늦는 문제가 있어, 원시 이벤트(RESUMED/PAUSED)를 직접 합산한다.
 * 화면 꺼짐/잠금 화면 이벤트에서도 구간을 닫아 화면이 꺼진 채 켜둔 앱이 계속 집계되지 않게 한다.
 */
class UsageTracker(private val context: Context) {
    private val usm = context.getSystemService(UsageStatsManager::class.java)
    private val appOps = context.getSystemService(AppOpsManager::class.java)

    /** 자녀가 설정에서 "사용 기록 접근"을 끄면 false → 변조로 간주해 잠금(fail-closed) */
    fun hasUsageAccess(): Boolean {
        val mode = appOps.unsafeCheckOpNoThrow(
            AppOpsManager.OPSTR_GET_USAGE_STATS, Process.myUid(), context.packageName,
        )
        return mode == AppOpsManager.MODE_ALLOWED
    }

    fun today(zone: ZoneId, now: Instant = Instant.now()): UsageSnapshot {
        val localDate = now.atZone(zone).toLocalDate()
        val dayStart = localDate.atStartOfDay(zone).toInstant().toEpochMilli()
        val end = now.toEpochMilli()
        // 자정 전에 시작해 계속 사용 중인 앱을 잡기 위해 3시간 앞부터 읽고 dayStart 로 잘라낸다
        val events = usm.queryEvents(dayStart - LOOKBACK_MS, end)
        val excluded = excludedPackages()

        val perApp = HashMap<String, Long>()
        var current: String? = null
        var since = 0L

        fun close(at: Long) {
            val pkg = current ?: return
            val from = maxOf(since, dayStart)
            val to = minOf(at, end)
            if (to > from && pkg !in excluded) perApp.merge(pkg, to - from, Long::plus)
            current = null
        }

        val e = UsageEvents.Event()
        while (events.hasNextEvent()) {
            events.getNextEvent(e)
            when (e.eventType) {
                UsageEvents.Event.ACTIVITY_RESUMED -> {
                    if (current != e.packageName) {
                        close(e.timeStamp)
                        current = e.packageName
                        since = e.timeStamp
                    }
                }
                UsageEvents.Event.ACTIVITY_PAUSED,
                UsageEvents.Event.ACTIVITY_STOPPED -> if (e.packageName == current) close(e.timeStamp)
                UsageEvents.Event.SCREEN_NON_INTERACTIVE,
                UsageEvents.Event.KEYGUARD_SHOWN,
                UsageEvents.Event.DEVICE_SHUTDOWN -> close(e.timeStamp)
            }
        }
        val foreground = current
        close(end) // 지금 사용 중인 앱의 진행 중 구간 포함

        val totalMs = perApp.values.sum()
        return UsageSnapshot(
            date = localDate.toString(),
            usedMinutes = (totalMs / 60_000).toInt(),
            perAppMinutes = perApp.mapValues { (it.value / 60_000).toInt() }.filterValues { it > 0 },
            foregroundPackage = foreground,
        )
    }

    /** 사용 시간에 넣지 않을 앱: 자기 자신(잠금 화면), 홈 런처, 시스템 UI */
    private fun excludedPackages(): Set<String> {
        val home = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
        val launcher = context.packageManager
            .resolveActivity(home, PackageManager.MATCH_DEFAULT_ONLY)?.activityInfo?.packageName
        return setOfNotNull(context.packageName, launcher, "com.android.systemui", "android")
    }

    private companion object { const val LOOKBACK_MS = 3 * 60 * 60 * 1000L }
}
