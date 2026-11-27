namespace XKeep.Agent.Service;

public sealed class AgentOptions
{
    /// <summary>블랙리스트 프로세스 주기 스캔 간격 (WMI 이벤트 누락 대비)</summary>
    public int ProcessScanIntervalSeconds { get; set; } = 5;

    /// <summary>사용 시간 집계·판정 주기</summary>
    public int SessionTickSeconds { get; set; } = 60;

    /// <summary>잠금 지시 후에도 세션이 계속 활성 상태면 이 시간 뒤 세션 연결 끊기(강제 로그인 화면)</summary>
    public int LockGraceSeconds { get; set; } = 120;
}
