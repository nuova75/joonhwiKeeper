package com.xkeep.agent.net

import com.xkeep.agent.policy.LockReason
import com.xkeep.agent.policy.Policy
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** 서버 zod 스키마와 동일하게: 판별자 필드명은 "type" */
val AgentJson = Json {
    ignoreUnknownKeys = true
    encodeDefaults = true
    explicitNulls = true
    classDiscriminator = "type"
}

// ---------- 에이전트 → 서버 ----------
@Serializable
sealed interface AgentMessage

@Serializable @SerialName("hello")
data class Hello(val agentVersion: String, val policyVersion: Int) : AgentMessage

@Serializable @SerialName("usage")
data class Usage(
    val date: String,
    val usedMinutes: Int,
    val perApp: Map<String, Int>,
    val locked: Boolean,
    val lockReason: LockReason?,
    val studyMinutes: Int = 0,
    /** "free" | "study" | "locked" */
    val mode: String = "free",
) : AgentMessage

@Serializable
data class AppInfo(val id: String, val label: String)

/** 설치된 앱 목록 (부모앱에서 학습 허용 앱을 고르는 데 사용) */
@Serializable @SerialName("apps")
data class Apps(val apps: List<AppInfo>) : AgentMessage

/** 아이의 "학습에 필요해요" 요청. kind: "site" | "app" */
@Serializable @SerialName("access_request")
data class AccessRequest(val kind: String, val value: String, val note: String = "") : AgentMessage

/** 아이가 직접 공부 시작 (10~240분). 끝내는 건 부모만 가능 */
@Serializable @SerialName("study_start")
data class StudyStart(val minutes: Int) : AgentMessage

@Serializable @SerialName("event")
data class Event(val kind: String, val detail: String, val at: String) : AgentMessage

@Serializable @SerialName("goodbye")
data class Goodbye(val reason: String) : AgentMessage

// ---------- 서버 → 에이전트 ----------
@Serializable
sealed interface ServerMessage

@Serializable @SerialName("policy")
data class PolicyMessage(val policy: Policy) : ServerMessage

/** 요청 처리 결과. decision: "today" | "always" | "deny" | "invalid" */
@Serializable @SerialName("request_result")
data class RequestResult(val requestId: Int? = null, val kind: String, val value: String, val decision: String) : ServerMessage

// ---------- REST ----------
@Serializable
data class PairRequest(
    val code: String,
    val platform: String = "android",
    val agentVersion: String,
    val fcmToken: String? = null,
)

@Serializable
data class PairResponse(val deviceId: String, val token: String, val policy: Policy)
