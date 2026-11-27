package com.xkeep.agent.usage

import android.content.Context

/**
 * 오늘 학습 모드에서 화면을 켜고 쓴 시간을 누적한다.
 * UsageStats 는 "언제 학습 모드였는지"를 모르므로 집행 루프의 매 판정마다 직접 센다.
 *  - 직전 판정도 학습 중 + 화면 켜짐이었을 때만 그 사이 시간을 더한다
 *  - 판정 간격이 비정상적으로 길면(절전 등) 최대 2분만 인정 (과대 집계 방지)
 * 하루 한도는 (전체 사용 - 학습 사용) 으로 계산하므로, 이 값이 과소 집계되면 아이에게 불리하고
 * 과대 집계되면 자유 시간이 늘어난다. 그래서 보수적으로(2분 상한) 센다.
 */
class StudyCounter(context: Context) {
    private val prefs = context.getSharedPreferences("study_counter", Context.MODE_PRIVATE)

    @Synchronized
    fun record(date: String, studying: Boolean, nowMs: Long = System.currentTimeMillis()): Int {
        var total = if (prefs.getString(KEY_DATE, null) == date) prefs.getLong(KEY_MS, 0) else 0L
        val last = prefs.getLong(KEY_LAST, 0)
        if (studying && last > 0) total += (nowMs - last).coerceIn(0, MAX_GAP_MS)
        prefs.edit()
            .putString(KEY_DATE, date)
            .putLong(KEY_MS, total)
            .putLong(KEY_LAST, if (studying) nowMs else 0)
            .apply()
        return (total / 60_000).toInt()
    }

    private companion object {
        const val KEY_DATE = "date"
        const val KEY_MS = "ms"
        const val KEY_LAST = "last"
        const val MAX_GAP_MS = 120_000L
    }
}
