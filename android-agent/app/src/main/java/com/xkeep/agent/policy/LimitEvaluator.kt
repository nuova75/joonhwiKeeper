package com.xkeep.agent.policy

import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneId
import java.time.ZonedDateTime

/** 판정 결과: 잠금(lock) 이 우선, 아니면 학습 모드 여부 */
data class Decision(val lock: LockReason?, val study: Boolean) {
    val mode: String get() = when { lock != null -> "locked"; study -> "study"; else -> "free" }
}

/**
 * 잠금·학습 판정 (순수 함수, 단위 테스트 대상).
 * 판정 순서는 세 플랫폼 공통: tamper → manual → bedtime → study → daily_limit
 *  - 학습 모드 중에는 하루 한도로 잠그지 않는다 (공부는 자유 시간을 쓰지 않음)
 *  - freeMinutes = 오늘 전체 사용 - 학습 모드 사용
 * Windows 쪽 XKeep.Agent.Shared.LimitEvaluator 와 동일한 규칙을 유지할 것.
 */
object LimitEvaluator {

    fun decide(policy: Policy, freeMinutes: Int, now: ZonedDateTime, tamper: Boolean = false): Decision {
        if (tamper) return Decision(LockReason.TAMPER, false)
        if (policy.manualLock) return Decision(LockReason.MANUAL, false)
        policy.bedtime?.let { if (inBedtime(it, now.toLocalTime())) return Decision(LockReason.BEDTIME, false) }
        if (isStudyActive(policy, now)) return Decision(null, true)
        val limit = effectiveLimit(policy, now) ?: return Decision(null, false)
        return Decision(if (freeMinutes >= limit) LockReason.DAILY_LIMIT else null, false)
    }

    fun evaluate(policy: Policy, usedMinutes: Int, now: ZonedDateTime, tamper: Boolean = false): LockReason? =
        decide(policy, usedMinutes, now, tamper).lock

    /** 부모/아이가 시작한 세션이 진행 중이거나, 시간표 칸 안에 있으면 학습 모드 */
    fun isStudyActive(policy: Policy, now: ZonedDateTime): Boolean = studyEndsAt(policy, now) != null

    /** 현재 학습 모드가 끝나는 시각 (학습 모드가 아니면 null). 세션과 시간표가 겹치면 늦은 쪽 */
    fun studyEndsAt(policy: Policy, now: ZonedDateTime): ZonedDateTime? {
        val session = policy.studySession
            ?.let { runCatching { OffsetDateTime.parse(it.until).atZoneSameInstant(now.zone) }.getOrNull() }
            ?.takeIf { it.isAfter(now) }
        val t = now.toLocalTime()
        val day = now.dayOfWeek.value // ISO 1=월
        val block = policy.study.schedule
            .filter { day in it.days && !t.isBefore(LocalTime.parse(it.start)) && t.isBefore(LocalTime.parse(it.end)) }
            .maxOfOrNull { LocalTime.parse(it.end) }
            ?.let { now.with(it) }
        return listOfNotNull(session, block).maxOrNull()
    }

    /** 학습 모드에서 허용되는 앱 = 학습 앱 + 오늘 승인된 앱 */
    fun studyApps(policy: Policy, now: ZonedDateTime): Set<String> =
        policy.study.apps.toSet() + todayGrants(policy, now)?.apps.orEmpty()

    fun studySites(policy: Policy, now: ZonedDateTime): List<String> =
        (policy.study.sites + todayGrants(policy, now)?.sites.orEmpty()).distinct()

    private fun todayGrants(policy: Policy, now: ZonedDateTime): Grants? =
        policy.grants?.takeIf { it.date == now.toLocalDate().toString() }

    /** 오늘 적용되는 한도 = 기본 한도 + 오늘 날짜의 추가 시간. null = 무제한 */
    fun effectiveLimit(policy: Policy, now: ZonedDateTime): Int? {
        val base = policy.dailyLimitMinutes ?: return null
        val extra = policy.extraTime?.takeIf { it.date == now.toLocalDate().toString() }?.minutes ?: 0
        return base + extra
    }

    fun remainingMinutes(policy: Policy, usedMinutes: Int, now: ZonedDateTime): Int? =
        effectiveLimit(policy, now)?.let { (it - usedMinutes).coerceAtLeast(0) }

    /** 자정을 넘기는 구간(22:00~07:00)도 처리. start == end 는 "없음"으로 취급 */
    fun inBedtime(bedtime: Bedtime, t: LocalTime): Boolean {
        val start = LocalTime.parse(bedtime.start)
        val end = LocalTime.parse(bedtime.end)
        return when {
            start == end -> false
            start < end -> !t.isBefore(start) && t.isBefore(end)
            else -> !t.isBefore(start) || t.isBefore(end)
        }
    }

    fun zoneOf(policy: Policy): ZoneId =
        runCatching { ZoneId.of(policy.timezone) }.getOrDefault(ZoneId.systemDefault())
}
