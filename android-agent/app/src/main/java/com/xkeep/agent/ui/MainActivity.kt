package com.xkeep.agent.ui

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.google.firebase.messaging.FirebaseMessaging
import com.xkeep.agent.BuildConfig
import com.xkeep.agent.XKeepApp
import com.xkeep.agent.net.AccessRequest
import com.xkeep.agent.net.StudyStart
import com.xkeep.agent.policy.LimitEvaluator
import com.xkeep.agent.policy.Policy
import java.time.ZonedDateTime
import com.xkeep.agent.service.EnforcementService
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume

/**
 * 자녀 기기 첫 화면.
 *  - 미페어링: 서버 주소 + 부모앱에서 받은 6자리 코드 입력
 *  - 페어링됨: 지금 모드·남은 시간, "공부 시작", "학습에 필요해요" 요청, 내 규칙(투명하게 공개), 보호 상태
 */
class MainActivity : ComponentActivity() {

    private val notifPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
        setContent {
            MaterialTheme {
                Surface(Modifier.fillMaxSize()) { Screen() }
            }
        }
    }

    @Composable
    private fun Screen() {
        val app = XKeepApp.from(this)
        var paired by remember { mutableStateOf(app.config.isPaired) }
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Text("xKeep 자녀 기기", style = MaterialTheme.typography.headlineSmall)
            if (paired) StatusPanel() else PairingPanel(onPaired = { paired = true })
        }
    }

    @Composable
    private fun PairingPanel(onPaired: () -> Unit) {
        val app = XKeepApp.from(this)
        val scope = rememberCoroutineScope()
        // 에뮬레이터에서 Mac 의 localhost 는 10.0.2.2
        var server by remember { mutableStateOf(if (BuildConfig.DEBUG) "http://10.0.2.2:8080" else "https://") }
        var code by remember { mutableStateOf("") }
        var busy by remember { mutableStateOf(false) }
        var error by remember { mutableStateOf<String?>(null) }

        OutlinedTextField(server, { server = it }, label = { Text("서버 주소") }, modifier = Modifier.fillMaxWidth())
        OutlinedTextField(
            code, { code = it.filter(Char::isDigit).take(6) },
            label = { Text("페어링 코드 (6자리)") },
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            modifier = Modifier.fillMaxWidth(),
        )
        Button(
            enabled = !busy && code.length == 6,
            onClick = {
                busy = true; error = null
                scope.launch {
                    runCatching {
                        val res = app.api.pair(server.trim(), code, fcmTokenOrNull())
                        app.config.save(server.trim(), res.deviceId, res.token)
                        app.policyStore.save(res.policy)
                        if (app.policyManager.isDeviceOwner) app.policyManager.applyBaseline()
                        EnforcementService.start(this@MainActivity)
                    }.onSuccess { onPaired() }.onFailure { error = it.message }
                    busy = false
                }
            },
        ) { Text(if (busy) "연결 중…" else "연결") }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
    }

    @Composable
    private fun StatusPanel() {
        val app = XKeepApp.from(this)
        val state by app.state.collectAsStateWithLifecycle()
        val connected by app.socket.connected.collectAsStateWithLifecycle()
        val policy by app.policyStore.policy.collectAsStateWithLifecycle()

        // 지금 상태 (아이가 가장 먼저 보는 정보)
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                when {
                    state.lockReason != null -> Text("🔒 ${state.lockReason!!.label}", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.error)
                    state.studyUntil != null -> Text("📚 학습 시간 · ${state.studyUntil}까지", style = MaterialTheme.typography.titleMedium)
                    else -> Text("자유 시간" + (state.remainingMinutes?.let { " · ${it}분 남음" } ?: ""), style = MaterialTheme.typography.titleMedium)
                }
                Text("오늘 학습 ${state.studyMinutes}분 · 자유 ${state.usedMinutes}분")
            }
        }

        if (state.lockReason == null && state.studyUntil == null) StudyStartPanel(connected)
        if (state.studyUntil != null) RequestPanel(connected, policy)
        policy?.let { RulesPanel(it) }

        // 보호 상태 (문제가 있을 때 확인용)
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Check("Device Owner", state.deviceOwner)
                Check("사용 기록 접근", state.usageAccess)
                Check("서버 연결", connected)
                Text("정책 버전: ${policy?.version ?: "-"}", style = MaterialTheme.typography.bodySmall)
            }
        }
        if (!state.usageAccess) {
            OutlinedButton(onClick = { startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS)) }) {
                Text("사용 기록 접근 허용하기")
            }
        }
        if (!state.deviceOwner) {
            Text(
                "Device Owner 가 아니면 앱 차단·잠금이 동작하지 않습니다. README 의 설정 절차를 따르세요.",
                color = MaterialTheme.colorScheme.error,
            )
        }
    }

    /** 아이가 스스로 공부 시작. 시작하면 끝날 때까지 학습 앱·사이트만 열린다 (중간 종료는 부모만) */
    @Composable
    private fun StudyStartPanel(connected: Boolean) {
        val app = XKeepApp.from(this)
        var sent by remember { mutableStateOf<Int?>(null) }
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("공부 시작하기", style = MaterialTheme.typography.titleSmall)
                Text("시작하면 정한 시간 동안 학습 앱·사이트만 열려요. 학습 시간은 자유 시간에서 빠지지 않아요.",
                    style = MaterialTheme.typography.bodySmall)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    for (m in listOf(30, 60, 90)) {
                        OutlinedButton(enabled = connected, onClick = {
                            if (app.socket.send(StudyStart(m))) sent = m
                        }) { Text("${m}분") }
                    }
                }
                if (!connected) Text("서버에 연결되면 시작할 수 있어요.", style = MaterialTheme.typography.bodySmall)
                sent?.let { Text("${it}분 학습 모드를 요청했어요.") }
            }
        }
    }

    /** "학습에 필요해요": 막힌 사이트·앱을 부모에게 요청 */
    @Composable
    private fun RequestPanel(connected: Boolean, policy: Policy?) {
        val app = XKeepApp.from(this)
        var url by remember { mutableStateOf("") }
        var note by remember { mutableStateOf("") }
        var done by remember { mutableStateOf<String?>(null) }
        var showApps by remember { mutableStateOf(false) }
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("학습에 필요한데 막혔나요?", style = MaterialTheme.typography.titleSmall)
                OutlinedTextField(url, { url = it }, label = { Text("사이트 주소 (예: ebsi.co.kr)") }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
                OutlinedTextField(note, { note = it.take(200) }, label = { Text("왜 필요한지 (선택)") }, singleLine = true,
                    modifier = Modifier.fillMaxWidth())
                Button(enabled = connected && url.isNotBlank(), onClick = {
                    if (app.socket.send(AccessRequest("site", url.trim(), note.trim()))) {
                        done = "부모님께 요청을 보냈어요. 결과는 알림으로 알려드려요."; url = ""; note = ""
                    }
                }) { Text("사이트 요청") }
                OutlinedButton(onClick = { showApps = !showApps }) { Text(if (showApps) "앱 목록 닫기" else "앱 요청하기") }
                if (showApps && policy != null) {
                    val allowed = LimitEvaluator.studyApps(policy, ZonedDateTime.now(LimitEvaluator.zoneOf(policy)))
                    val apps = remember { app.policyManager.launchableApps() }.filter { it.first !in allowed }
                    apps.forEach { (pkg, label) ->
                        Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                            Text(label, Modifier.weight(1f))
                            OutlinedButton(enabled = connected, onClick = {
                                if (app.socket.send(AccessRequest("app", pkg, note.trim()))) done = "‘$label’ 사용을 요청했어요."
                            }) { Text("요청") }
                        }
                    }
                }
                done?.let { Text(it) }
            }
        }
    }

    /** 내 규칙: 부모가 정한 규칙을 아이도 그대로 볼 수 있게 공개 */
    @Composable
    private fun RulesPanel(p: Policy) {
        val days = listOf("월", "화", "수", "목", "금", "토", "일")
        Card(Modifier.fillMaxWidth()) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text("내 규칙", style = MaterialTheme.typography.titleSmall)
                Text("하루 자유 시간: " + (p.dailyLimitMinutes?.let { "${it}분" } ?: "제한 없음"))
                p.bedtime?.let { Text("잠자는 시간: ${it.start} ~ ${it.end}") }
                if (p.study.schedule.isEmpty()) Text("학습 시간표: 없음")
                p.study.schedule.forEach { b ->
                    Text("학습: ${b.days.sorted().joinToString("") { days[it - 1] }} ${b.start} ~ ${b.end}")
                }
                Text("학습 사이트: " + p.study.sites.joinToString(", ").ifEmpty { "없음" },
                    style = MaterialTheme.typography.bodySmall)
            }
        }
    }

    @Composable
    private fun Check(label: String, ok: Boolean) {
        Text("${if (ok) "✅" else "❌"} $label", color = if (ok) Color.Unspecified else MaterialTheme.colorScheme.error)
    }

    /** google-services.json 없이 빌드한 경우 Firebase 미초기화 → null */
    private suspend fun fcmTokenOrNull(): String? = runCatching {
        suspendCancellableCoroutine { cont ->
            FirebaseMessaging.getInstance().token
                .addOnSuccessListener { cont.resume(it) }
                .addOnFailureListener { cont.resume(null) }
        }
    }.getOrNull()
}
