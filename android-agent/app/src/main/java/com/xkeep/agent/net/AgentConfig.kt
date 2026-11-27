package com.xkeep.agent.net

import android.content.Context

/** 페어링 결과(서버 주소, 기기 ID, 기기 토큰) 보관 */
class AgentConfig(context: Context) {
    private val prefs = context.getSharedPreferences("agent_config", Context.MODE_PRIVATE)

    val serverUrl: String? get() = prefs.getString("serverUrl", null)
    val deviceId: String? get() = prefs.getString("deviceId", null)
    val token: String? get() = prefs.getString("token", null)
    val isPaired: Boolean get() = serverUrl != null && token != null

    fun save(serverUrl: String, deviceId: String, token: String) {
        prefs.edit()
            .putString("serverUrl", serverUrl.trimEnd('/'))
            .putString("deviceId", deviceId)
            .putString("token", token)
            .commit()
    }

    fun clear() { prefs.edit().clear().commit() }

    /** https://host → wss://host/ws/agent, http://host → ws://host/ws/agent */
    fun webSocketUrl(): String? = serverUrl?.let {
        it.replaceFirst(Regex("^http"), "ws") + "/ws/agent"
    }
}
