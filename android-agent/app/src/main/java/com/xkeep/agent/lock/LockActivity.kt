package com.xkeep.agent.lock

import android.content.Intent
import android.os.Bundle
import android.telecom.TelecomManager
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.xkeep.agent.XKeepApp
import kotlinx.coroutines.launch

/**
 * 전체 화면 잠금. Device Owner 가 setLockTaskPackages 로 허용했으므로
 * startLockTask() 시 사용자 확인 없이 키오스크 모드로 진입한다 (홈·최근 앱·알림창 차단).
 * 잠금 사유가 사라지면 stopLockTask() 후 스스로 종료한다.
 */
class LockActivity : ComponentActivity() {

    private val app get() = XKeepApp.from(this)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 뒤로 가기 무시
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = Unit
        })

        setContent {
            val state by app.state.collectAsStateWithLifecycle()
            Column(
                modifier = Modifier.fillMaxSize().background(Color(0xFF14332B)).padding(32.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("🔒", fontSize = 64.sp)
                Spacer(Modifier.height(24.dp))
                Text(
                    state.lockReason?.label ?: "잠금 해제 중…",
                    color = Color.White, fontSize = 22.sp, textAlign = TextAlign.Center,
                )
                Spacer(Modifier.height(12.dp))
                Text(
                    "오늘 사용: ${state.usedMinutes}분",
                    color = Color(0xFFB8D8CE), style = MaterialTheme.typography.bodyLarge,
                )
                Spacer(Modifier.height(40.dp))
                OutlinedButton(onClick = ::openDialer) { Text("전화 걸기", color = Color.White) }
            }
        }

        // 잠금 사유가 사라지면 종료
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                app.state.collect { if (it.lockReason == null) unlockAndFinish() }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        if (app.state.value.lockReason != null && app.policyManager.isLockTaskPermitted()) {
            runCatching { startLockTask() }
        }
    }

    private fun unlockAndFinish() {
        runCatching { stopLockTask() }
        finish()
    }

    /** 잠금 중에도 전화는 가능해야 한다 (기본 전화 앱은 LockTask 허용 목록에 포함됨) */
    private fun openDialer() {
        val dialer = getSystemService(TelecomManager::class.java)?.defaultDialerPackage ?: return
        runCatching { startActivity(Intent(Intent.ACTION_DIAL).setPackage(dialer)) }
    }
}
