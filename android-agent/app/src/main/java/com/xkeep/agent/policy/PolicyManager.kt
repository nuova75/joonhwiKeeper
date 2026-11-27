package com.xkeep.agent.policy

import android.app.admin.DevicePolicyManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.UserManager
import android.provider.Settings
import android.telecom.TelecomManager
import android.util.Log
import com.xkeep.agent.BuildConfig
import com.xkeep.agent.admin.AgentAdminReceiver

/**
 * Device Owner 권한으로 OS 수준 제한을 적용하는 유일한 클래스.
 * DevicePolicyManager 호출은 모두 여기에 모은다 (다른 곳에서 dpm 직접 호출 금지).
 *
 * 두 가지 계층:
 *  1. applyBaseline()   — 페어링/부팅 시 1회: 우회 경로 자체를 막는 사용자 제한
 *  2. applyEnforcement() — 매 분/정책 변경 시: 차단 앱·시간 초과 잠금·학습 모드에 따른 패키지 정지 상태 동기화
 *  3. applyBrowserPolicy() — 학습 모드일 때 Chrome 에 사이트 허용 목록(관리 구성) 적용
 */
class PolicyManager(private val context: Context) {

    private val dpm = context.getSystemService(DevicePolicyManager::class.java)
    private val admin = AgentAdminReceiver.componentName(context)
    private val pm: PackageManager = context.packageManager
    private val prefs = context.getSharedPreferences("policy_manager", Context.MODE_PRIVATE)

    val isDeviceOwner: Boolean
        get() = dpm.isDeviceOwnerApp(context.packageName)

    /**
     * 우회 방지 기본 정책.
     * - 앱 삭제 차단, 안전 모드 부팅 차단(안전 모드에서는 서드파티 앱이 실행되지 않아 제한이 풀림)
     * - 설정 앱에서 강제 종료/데이터 삭제 차단(DISALLOW_APPS_CONTROL)
     * - 시간 조작 차단(날짜를 바꿔 "오늘 사용 시간"을 초기화하는 우회 방지)
     * - 사용자 추가 차단(다른 사용자 프로필로 전환하는 우회 방지), 출처 불명 앱 설치 차단
     */
    fun applyBaseline() {
        requireOwner()
        dpm.setUninstallBlocked(admin, context.packageName, true)

        val restrictions = buildList {
            add(UserManager.DISALLOW_SAFE_BOOT)
            add(UserManager.DISALLOW_FACTORY_RESET)
            add(UserManager.DISALLOW_ADD_USER)
            add(UserManager.DISALLOW_REMOVE_USER)
            add(UserManager.DISALLOW_APPS_CONTROL)
            add(UserManager.DISALLOW_CONFIG_DATE_TIME)
            add(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                add(UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES_GLOBALLY)
            }
        }
        restrictions.forEach { dpm.addUserRestriction(admin, it) }

        // 디버그 빌드에서는 adb 를 써야 하므로 개발자 옵션을 열어 둔다. 릴리스에서는 차단.
        if (BuildConfig.DEBUG) {
            dpm.clearUserRestriction(admin, UserManager.DISALLOW_DEBUGGING_FEATURES)
        } else {
            dpm.addUserRestriction(admin, UserManager.DISALLOW_DEBUGGING_FEATURES)
        }

        // 자동 시간 강제
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            dpm.setAutoTimeEnabled(admin, true)
        } else {
            @Suppress("DEPRECATION")
            dpm.setAutoTimeRequired(admin, true)
        }

        // LockTask(키오스크) 허용 패키지: 잠금 화면(자기 자신) + 긴급 전화용 기본 전화 앱
        val lockTaskPkgs = listOfNotNull(context.packageName, defaultDialer()).distinct()
        dpm.setLockTaskPackages(admin, lockTaskPkgs.toTypedArray())
        // 잠금 중 허용 기능: 전원 메뉴만(전원 끄기/긴급 통화). 홈·최근 앱·알림창은 막힘
        dpm.setLockTaskFeatures(admin, DevicePolicyManager.LOCK_TASK_FEATURE_GLOBAL_ACTIONS)

        Log.i(TAG, "baseline applied: $restrictions")
    }

    /**
     * 정지(suspend) 상태를 원하는 상태로 동기화한다. 멱등이라 매 분 호출해도 안전.
     *
     * setPackagesSuspended vs setApplicationHidden:
     *  - suspend: 아이콘은 보이지만 회색 처리되고 실행 시 "관리자가 일시중지함" 안내. 데이터 보존, 즉시 복구.
     *  - hidden : 앱이 사라진 것처럼 보임(사실상 비활성화). 아이들 혼란이 크고 위젯/알림도 끊김.
     *  → MVP 는 suspend 를 사용한다. 정지된 앱은 알림도 표시되지 않는다.
     *
     * @param blocked       항상 정지할 앱(부모가 지정한 차단 목록)
     * @param lockAll       true 면 실행 가능한 모든 앱을 정지(시간 초과/취침/부모 잠금)
     * @param alwaysAllowed lockAll/학습 모드 중에도 정지하지 않을 앱
     * @param studyAllowed  null 이 아니면 학습 모드: 이 목록(+alwaysAllowed) 외의 실행 가능한 앱을 모두 정지
     * @return 정지에 실패한 패키지 (시스템 필수 앱 등은 OS 가 거부)
     */
    fun applyEnforcement(
        blocked: Set<String>,
        lockAll: Boolean,
        alwaysAllowed: Set<String>,
        studyAllowed: Set<String>? = null,
    ): List<String> {
        requireOwner()
        val protected = protectedPackages()
        val target = buildSet {
            addAll(blocked.filter(::isInstalled))
            if (lockAll) addAll(launchablePackages() - alwaysAllowed)
            else if (studyAllowed != null) addAll(launchablePackages() - alwaysAllowed - studyAllowed)
            removeAll(protected)
        }

        val previously = prefs.getStringSet(KEY_SUSPENDED, emptySet()).orEmpty()
        val toRelease = previously - target
        if (toRelease.isNotEmpty()) {
            dpm.setPackagesSuspended(admin, toRelease.toTypedArray(), false)
        }
        // 이미 정지된 것도 다시 호출: 차단 목록에 있던 앱이 새로 설치된 경우까지 커버
        val failed = if (target.isNotEmpty()) {
            dpm.setPackagesSuspended(admin, target.toTypedArray(), true).toList()
        } else emptyList()

        prefs.edit().putStringSet(KEY_SUSPENDED, target - failed.toSet()).apply()
        if (failed.isNotEmpty()) Log.w(TAG, "suspend failed: $failed")
        return failed
    }

    /**
     * Chrome 관리 구성(엔터프라이즈 정책)으로 사이트 허용 목록 적용.
     *  - 학습 모드(sites != null): 모든 URL 차단 + 허용 목록만 예외, 시크릿 모드 끔
     *  - 자유 시간(sites == null): 제한 해제
     * 학습 모드에서는 다른 브라우저(삼성 인터넷 등)가 허용 앱 목록에 없으므로 정지되어,
     * 웹은 Chrome 하나로 모인다. Chrome 은 구성 변경을 실행 중에도 반영한다.
     * 같은 내용이면 다시 쓰지 않는다 (Chrome 이 매 분 정책을 다시 읽지 않도록).
     */
    fun applyBrowserPolicy(sites: List<String>?) {
        requireOwner()
        val key = sites?.sorted()?.joinToString("\n") ?: "<free>"
        if (prefs.getString(KEY_BROWSER, null) == key) return
        val bundle = Bundle()
        if (sites != null) {
            bundle.putStringArray("URLBlocklist", arrayOf("*"))
            bundle.putStringArray("URLAllowlist", sites.toTypedArray())
            bundle.putInt("IncognitoModeAvailability", 1) // 1 = 시크릿 모드 사용 불가
        }
        for (browser in CHROME_PACKAGES) {
            runCatching { dpm.setApplicationRestrictions(admin, browser, bundle) }
                .onFailure { Log.w(TAG, "chrome restrictions failed: $browser", it) }
        }
        prefs.edit().putString(KEY_BROWSER, key).apply()
        Log.i(TAG, "browser policy: ${if (sites == null) "free" else "allow ${sites.size} sites"}")
    }

    /** 실행 가능한(런처에 보이는) 앱 목록: 부모앱에서 학습 허용 앱을 고를 때 쓴다 */
    fun launchableApps(): List<Pair<String, String>> {
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        @Suppress("DEPRECATION")
        val infos = pm.queryIntentActivities(intent, 0)
        return infos
            .map { it.activityInfo.packageName to it.loadLabel(pm).toString() }
            .filter { it.first != context.packageName }
            .distinctBy { it.first }
            .sortedBy { it.second }
    }

    fun isSuspendedByUs(pkg: String): Boolean =
        prefs.getStringSet(KEY_SUSPENDED, emptySet()).orEmpty().contains(pkg)

    fun isLockTaskPermitted(): Boolean = dpm.isLockTaskPermitted(context.packageName)

    /**
     * 개발/해제용: 모든 제한을 풀고 Device Owner 를 내려놓는다.
     * 릴리스에서는 부모 인증 후에만 호출되도록 연결해야 한다 (MVP 에서는 UI 미연결).
     */
    fun releaseOwnership() {
        requireOwner()
        applyEnforcement(emptySet(), lockAll = false, alwaysAllowed = emptySet())
        applyBrowserPolicy(null)
        listOf(
            UserManager.DISALLOW_SAFE_BOOT, UserManager.DISALLOW_FACTORY_RESET,
            UserManager.DISALLOW_ADD_USER, UserManager.DISALLOW_REMOVE_USER,
            UserManager.DISALLOW_APPS_CONTROL, UserManager.DISALLOW_CONFIG_DATE_TIME,
            UserManager.DISALLOW_INSTALL_UNKNOWN_SOURCES, UserManager.DISALLOW_DEBUGGING_FEATURES,
        ).forEach { dpm.clearUserRestriction(admin, it) }
        dpm.setUninstallBlocked(admin, context.packageName, false)
        dpm.setLockTaskPackages(admin, emptyArray())
        @Suppress("DEPRECATION")
        dpm.clearDeviceOwnerApp(context.packageName)
    }

    // ---------------- helpers ----------------

    /** 어떤 경우에도 정지하면 안 되는 앱: 자기 자신, 홈 런처, 전화, 키보드 */
    private fun protectedPackages(): Set<String> = setOfNotNull(
        context.packageName,
        "android",
        "com.android.systemui",
        defaultDialer(),
        defaultLauncher(),
        defaultInputMethod(),
    )

    private fun launchablePackages(): Set<String> {
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)
        val infos = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            pm.queryIntentActivities(intent, PackageManager.ResolveInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION") pm.queryIntentActivities(intent, 0)
        }
        return infos.mapTo(mutableSetOf()) { it.activityInfo.packageName }
    }

    private fun isInstalled(pkg: String): Boolean = runCatching {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            pm.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION") pm.getPackageInfo(pkg, 0)
        }
    }.isSuccess

    private fun defaultDialer(): String? =
        context.getSystemService(TelecomManager::class.java)?.defaultDialerPackage

    private fun defaultLauncher(): String? {
        val intent = Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_HOME)
        return pm.resolveActivity(intent, PackageManager.MATCH_DEFAULT_ONLY)?.activityInfo?.packageName
    }

    private fun defaultInputMethod(): String? =
        Settings.Secure.getString(context.contentResolver, Settings.Secure.DEFAULT_INPUT_METHOD)
            ?.substringBefore('/')

    private fun requireOwner() {
        check(isDeviceOwner) { "Device Owner 가 아닙니다. README 의 dpm set-device-owner 절차를 먼저 수행하세요." }
    }

    companion object {
        private const val TAG = "PolicyManager"
        private const val KEY_SUSPENDED = "suspended_by_us"
        private const val KEY_BROWSER = "browser_policy"
        /** 정식 Chrome + 베타/개발 채널 (설치된 것에만 의미 있음) */
        private val CHROME_PACKAGES = listOf("com.android.chrome", "com.chrome.beta", "com.chrome.dev")
    }
}
