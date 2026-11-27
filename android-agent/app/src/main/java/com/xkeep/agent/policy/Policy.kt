package com.xkeep.agent.policy

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/** shared/protocol/PROTOCOL.md 의 Policy 와 필드명이 1:1 대응 */
@Serializable
data class Policy(
    val version: Int,
    val timezone: String = "Asia/Seoul",
    val dailyLimitMinutes: Int? = null,
    val bedtime: Bedtime? = null,
    val blockedApps: List<String> = emptyList(),
    val alwaysAllowed: List<String> = emptyList(),
    val manualLock: Boolean = false,
    val extraTime: ExtraTime? = null,
    val study: Study = Study(),
    val studySession: StudySession? = null,
    val grants: Grants? = null,
)

/** 학습 모드: 이 시간엔 apps/sites 에 있는 것만 사용 가능 */
@Serializable
data class Study(
    val apps: List<String> = emptyList(),
    val sites: List<String> = emptyList(),
    val schedule: List<StudyBlock> = emptyList(),
)

/** days: ISO 요일 1=월 … 7=일, start < end */
@Serializable
data class StudyBlock(val days: List<Int>, val start: String, val end: String)

/** until: ISO-8601 (오프셋 포함), by: "parent" | "child" */
@Serializable
data class StudySession(val until: String, val by: String)

/** 요청 승인으로 오늘만 허용된 앱·사이트 */
@Serializable
data class Grants(val date: String, val apps: List<String> = emptyList(), val sites: List<String> = emptyList())

@Serializable
data class Bedtime(val start: String, val end: String)

@Serializable
data class ExtraTime(val date: String, val minutes: Int)

/** 잠금 사유. 직렬화 값은 서버 lockReason enum 과 동일 */
@Serializable
enum class LockReason {
    @SerialName("tamper") TAMPER,
    @SerialName("manual") MANUAL,
    @SerialName("bedtime") BEDTIME,
    @SerialName("daily_limit") DAILY_LIMIT;

    val label: String
        get() = when (this) {
            TAMPER -> "보호 설정이 변경되어 잠겼어요. 부모님께 알려주세요."
            MANUAL -> "부모님이 잠갔어요."
            BEDTIME -> "잘 시간이에요."
            DAILY_LIMIT -> "오늘 사용 시간을 다 썼어요."
        }
}
