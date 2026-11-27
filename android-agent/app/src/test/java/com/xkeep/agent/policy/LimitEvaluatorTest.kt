package com.xkeep.agent.policy

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZonedDateTime

class LimitEvaluatorTest {
    private val seoul = ZoneId.of("Asia/Seoul")
    private val noon = ZonedDateTime.of(2026, 11, 25, 12, 0, 0, 0, seoul)
    private val base = Policy(version = 1, dailyLimitMinutes = 60)

    @Test fun underLimit_notLocked() = assertNull(LimitEvaluator.evaluate(base, 59, noon))

    @Test fun atLimit_locked() = assertEquals(LockReason.DAILY_LIMIT, LimitEvaluator.evaluate(base, 60, noon))

    @Test fun extraTimeToday_extendsLimit() {
        val p = base.copy(extraTime = ExtraTime("2026-11-25", 30))
        assertNull(LimitEvaluator.evaluate(p, 80, noon))
        assertEquals(LockReason.DAILY_LIMIT, LimitEvaluator.evaluate(p, 90, noon))
    }

    @Test fun extraTimeOtherDay_ignored() {
        val p = base.copy(extraTime = ExtraTime("2026-11-24", 30))
        assertEquals(LockReason.DAILY_LIMIT, LimitEvaluator.evaluate(p, 60, noon))
    }

    @Test fun noLimit_neverLockedByUsage() =
        assertNull(LimitEvaluator.evaluate(base.copy(dailyLimitMinutes = null), 1000, noon))

    @Test fun precedence_tamperThenManualThenBedtime() {
        val p = base.copy(manualLock = true, bedtime = Bedtime("11:00", "13:00"))
        assertEquals(LockReason.TAMPER, LimitEvaluator.evaluate(p, 0, noon, tamper = true))
        assertEquals(LockReason.MANUAL, LimitEvaluator.evaluate(p, 0, noon))
        assertEquals(LockReason.BEDTIME, LimitEvaluator.evaluate(p.copy(manualLock = false), 0, noon))
    }

    @Test fun bedtimeAcrossMidnight() {
        val b = Bedtime("22:00", "07:00")
        assertTrue(LimitEvaluator.inBedtime(b, LocalTime.of(23, 30)))
        assertTrue(LimitEvaluator.inBedtime(b, LocalTime.of(6, 59)))
        assertFalse(LimitEvaluator.inBedtime(b, LocalTime.of(7, 0)))
        assertFalse(LimitEvaluator.inBedtime(b, LocalTime.of(21, 59)))
        assertFalse(LimitEvaluator.inBedtime(Bedtime("22:00", "22:00"), LocalTime.of(22, 0)))
    }

    // ---------- 학습 모드 (2026-11-25 는 수요일 = ISO 3) ----------
    private val evening = ZonedDateTime.of(2026, 11, 25, 19, 30, 0, 0, seoul)
    private val weekdayStudy = base.copy(study = Study(schedule = listOf(StudyBlock(listOf(1, 2, 3, 4, 5), "19:00", "21:00"))))

    @Test fun schedule_activatesStudy_andSuspendsDailyLimit() {
        val d = LimitEvaluator.decide(weekdayStudy, 999, evening)
        assertTrue(d.study)
        assertNull(d.lock) // 학습 중에는 한도 초과여도 잠그지 않음
        assertEquals(LocalTime.of(21, 0), LimitEvaluator.studyEndsAt(weekdayStudy, evening)!!.toLocalTime())
    }

    @Test fun schedule_otherDayOrTime_free() {
        assertFalse(LimitEvaluator.isStudyActive(weekdayStudy, evening.withHour(21)))
        val saturday = ZonedDateTime.of(2026, 11, 28, 19, 30, 0, 0, seoul)
        assertFalse(LimitEvaluator.isStudyActive(weekdayStudy, saturday))
    }

    @Test fun bedtimeAndManual_beatStudy() {
        val p = weekdayStudy.copy(bedtime = Bedtime("19:00", "07:00"))
        assertEquals(LockReason.BEDTIME, LimitEvaluator.decide(p, 0, evening).lock)
        assertEquals(LockReason.MANUAL, LimitEvaluator.decide(weekdayStudy.copy(manualLock = true), 0, evening).lock)
    }

    @Test fun session_activeUntilExpiry() {
        val p = base.copy(studySession = StudySession("2026-11-25T13:00:00+09:00", "child"))
        assertTrue(LimitEvaluator.isStudyActive(p, noon))
        assertFalse(LimitEvaluator.isStudyActive(p, noon.withHour(13)))
    }

    @Test fun grants_onlyToday() {
        val p = base.copy(
            study = Study(apps = listOf("a"), sites = listOf("ebsi.co.kr")),
            grants = Grants("2026-11-25", apps = listOf("b"), sites = listOf("youtube.com")),
        )
        assertEquals(setOf("a", "b"), LimitEvaluator.studyApps(p, noon))
        assertEquals(listOf("ebsi.co.kr", "youtube.com"), LimitEvaluator.studySites(p, noon))
        assertEquals(setOf("a"), LimitEvaluator.studyApps(p, noon.plusDays(1)))
    }
}
