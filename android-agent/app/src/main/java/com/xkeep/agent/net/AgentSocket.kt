package com.xkeep.agent.net

import android.util.Log
import com.xkeep.agent.BuildConfig
import com.xkeep.agent.policy.PolicyStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * 서버와의 상시 WebSocket 연결.
 *  - 접속 즉시 hello(policyVersion) 전송 → 서버가 버전이 다르면 최신 정책을 내려줌
 *  - 끊기면 지수 백오프(2s → 최대 60s)로 재접속
 *  - 절전(Doze)으로 네트워크가 막혀 있을 때는 서버가 FCM 으로 깨워 connectNow() 를 호출
 */
class AgentSocket(
    private val http: OkHttpClient,
    private val config: AgentConfig,
    private val store: PolicyStore,
    private val scope: CoroutineScope,
) {
    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected

    /** 요청 처리 결과 (서비스가 구독해서 아이에게 알림으로 보여줌) */
    private val _results = MutableSharedFlow<RequestResult>(extraBufferCapacity = 16)
    val results: SharedFlow<RequestResult> = _results

    @Volatile private var ws: WebSocket? = null
    private var reconnectJob: Job? = null
    private var attempt = 0
    @Volatile private var stopped = true

    fun start() {
        stopped = false
        if (ws == null) connectNow()
    }

    fun stop() {
        stopped = true
        reconnectJob?.cancel()
        ws?.close(1000, "stop")
        ws = null
    }

    @Synchronized
    fun connectNow() {
        if (stopped) return
        val url = config.webSocketUrl() ?: return
        val token = config.token ?: return
        reconnectJob?.cancel()
        ws?.cancel()
        val req = Request.Builder().url(url).header("Authorization", "Bearer $token").build()
        ws = http.newWebSocket(req, Listener())
    }

    /** @return 전송 큐에 들어갔으면 true (오프라인이면 false — 사용량은 다음 분에 다시 보냄) */
    fun send(msg: AgentMessage): Boolean {
        val socket = ws ?: return false
        if (!_connected.value) return false
        return socket.send(AgentJson.encodeToString(AgentMessage.serializer(), msg))
    }

    private fun scheduleReconnect() {
        if (stopped) return
        val backoff = (2_000L shl attempt.coerceAtMost(5)).coerceAtMost(60_000L)
        attempt++
        reconnectJob = scope.launch {
            delay(backoff)
            connectNow()
        }
    }

    private inner class Listener : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            if (webSocket !== ws) return
            attempt = 0
            _connected.value = true
            val version = store.policy.value?.version ?: 0
            send(Hello(BuildConfig.VERSION_NAME, version))
            Log.i(TAG, "connected")
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            val msg = runCatching { AgentJson.decodeFromString(ServerMessage.serializer(), text) }
                .getOrElse { Log.w(TAG, "unknown message: $text"); return }
            when (msg) {
                is PolicyMessage -> {
                    // 서버가 진실의 원천: 버전 비교 없이 서버 정책으로 덮어씀
                    store.save(msg.policy)
                    Log.i(TAG, "policy v${msg.policy.version} received")
                }
                is RequestResult -> _results.tryEmit(msg)
            }
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = onDown(webSocket, "closed $code $reason")

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) =
            onDown(webSocket, "failure ${t.message}")

        private fun onDown(webSocket: WebSocket, why: String) {
            if (webSocket !== ws) return
            _connected.value = false
            ws = null
            Log.w(TAG, "disconnected: $why")
            // 4401 = 인증 실패(기기 삭제됨) → 재접속하지 않음
            if (!why.startsWith("closed 4401") && !why.startsWith("closed 4001")) scheduleReconnect()
        }
    }

    private companion object { const val TAG = "AgentSocket" }
}
