package com.xkeep.agent.policy

import android.content.Context
import com.xkeep.agent.net.AgentJson
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * 서버에서 받은 마지막 정책의 로컬 캐시.
 * 오프라인에서도 이 정책으로 계속 제한한다 (PROTOCOL.md "판정은 에이전트가 한다").
 * DISALLOW_APPS_CONTROL 로 자녀가 앱 데이터를 지울 수 없어 SharedPreferences 로 충분.
 */
class PolicyStore(context: Context) {
    private val prefs = context.getSharedPreferences("policy_store", Context.MODE_PRIVATE)
    private val _policy = MutableStateFlow(load())
    val policy: StateFlow<Policy?> = _policy.asStateFlow()

    /** @return 실제로 바뀌었으면 true */
    @Synchronized
    fun save(policy: Policy): Boolean {
        if (policy == _policy.value) return false
        prefs.edit().putString(KEY, AgentJson.encodeToString(Policy.serializer(), policy)).commit()
        _policy.value = policy
        return true
    }

    fun clear() {
        prefs.edit().clear().commit()
        _policy.value = null
    }

    private fun load(): Policy? = prefs.getString(KEY, null)?.let {
        runCatching { AgentJson.decodeFromString(Policy.serializer(), it) }.getOrNull()
    }

    private companion object { const val KEY = "policy" }
}
