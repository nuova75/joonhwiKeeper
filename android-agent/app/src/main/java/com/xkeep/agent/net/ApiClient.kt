package com.xkeep.agent.net

import com.xkeep.agent.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

class ApiClient(private val http: OkHttpClient, private val config: AgentConfig) {

    /** 부모앱에서 발급한 6자리 코드로 페어링 */
    suspend fun pair(serverUrl: String, code: String, fcmToken: String?): PairResponse = withContext(Dispatchers.IO) {
        val body = AgentJson.encodeToString(
            PairRequest.serializer(),
            PairRequest(code = code, agentVersion = BuildConfig.VERSION_NAME, fcmToken = fcmToken),
        )
        val req = Request.Builder()
            .url("${serverUrl.trimEnd('/')}/api/agent/pair")
            .post(body.toRequestBody(JSON))
            .build()
        http.newCall(req).execute().use { res ->
            val text = res.body?.string().orEmpty()
            if (!res.isSuccessful) throw IllegalStateException("페어링 실패 (${res.code}): $text")
            AgentJson.decodeFromString(PairResponse.serializer(), text)
        }
    }

    suspend fun updateFcmToken(fcmToken: String) = withContext(Dispatchers.IO) {
        val server = config.serverUrl ?: return@withContext
        val token = config.token ?: return@withContext
        val body = buildJsonObject { put("fcmToken", fcmToken) }.toString()
        val req = Request.Builder()
            .url("$server/api/agent/fcm-token")
            .header("Authorization", "Bearer $token")
            .post(body.toRequestBody(JSON))
            .build()
        runCatching { http.newCall(req).execute().close() }
    }

    private companion object { val JSON = "application/json".toMediaType() }
}
