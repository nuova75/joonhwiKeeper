using Microsoft.Win32;

namespace XKeep.Agent.Service.Enforcement;

/// <summary>
/// 학습 모드의 사이트 허용 목록을 Chrome·Edge 엔터프라이즈 정책(HKLM 레지스트리)으로 적용한다.
///  - 학습 모드: URLBlocklist=["*"], URLAllowlist=[허용 사이트], 시크릿/InPrivate 끔
///  - 자유 시간: 위 값 제거
/// HKLM\SOFTWARE\Policies 는 관리자만 쓸 수 있어 표준 사용자(자녀)가 지우거나 바꿀 수 없다.
/// Chrome/Edge 는 실행 중에도 이 키 변경을 감지해 몇 초 안에 반영한다(chrome://policy 에서 확인 가능).
/// Firefox 등 다른 브라우저는 학습 허용 프로그램 목록에 없으면 실행 즉시 종료되므로 따로 다루지 않는다.
/// </summary>
public sealed class BrowserPolicy(ILogger<BrowserPolicy> log)
{
    private sealed record Browser(string Key, string PrivateModeValue);

    private static readonly Browser[] Browsers =
    [
        new(@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability"),
    ];

    private string? _applied;

    /// <param name="sites">null 이면 자유 시간(제한 해제)</param>
    public void Apply(IReadOnlyList<string>? sites)
    {
        var key = sites is null ? "<free>" : string.Join('\n', sites.Order(StringComparer.Ordinal));
        if (key == _applied) return; // 같은 내용이면 다시 쓰지 않음 (브라우저 정책 재로딩 방지)

        foreach (var b in Browsers)
        {
            try
            {
                if (sites is null) Clear(b);
                else Write(b, sites);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "브라우저 정책 적용 실패: {Key}", b.Key);
                return; // 다음 틱에 재시도
            }
        }
        _applied = key;
        log.LogInformation("브라우저 정책: {Mode}", sites is null ? "자유" : $"학습 (허용 {sites.Count}개)");
    }

    private static void Write(Browser b, IReadOnlyList<string> sites)
    {
        using var root = Registry.LocalMachine.CreateSubKey(b.Key, writable: true);
        root.SetValue(b.PrivateModeValue, 1, RegistryValueKind.DWord); // 1 = 사용 불가
        ReplaceList(root, "URLBlocklist", ["*"]);
        ReplaceList(root, "URLAllowlist", sites);
    }

    private static void Clear(Browser b)
    {
        using var root = Registry.LocalMachine.OpenSubKey(b.Key, writable: true);
        if (root is null) return;
        root.DeleteValue(b.PrivateModeValue, throwOnMissingValue: false);
        root.DeleteSubKeyTree("URLBlocklist", throwOnMissingSubKey: false);
        root.DeleteSubKeyTree("URLAllowlist", throwOnMissingSubKey: false);
    }

    /// <summary>목록형 정책은 하위 키에 "1","2",… 이름의 문자열 값으로 저장</summary>
    private static void ReplaceList(RegistryKey root, string name, IReadOnlyList<string> items)
    {
        root.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        using var list = root.CreateSubKey(name, writable: true);
        for (var i = 0; i < items.Count; i++) list.SetValue((i + 1).ToString(), items[i], RegistryValueKind.String);
    }
}
