#Requires -RunAsAdministrator
<#
  xKeep Windows 에이전트 설치 (관리자 PowerShell)
  사용 예:
    .\install.ps1 -Server https://xkeep.example.com -Code 123456
  사전: windows-agent 폴더에서 publish 완료 (README 참고) → .\publish\ 에 두 exe 가 있어야 함
#>
param(
    [Parameter(Mandatory)] [string] $Server,
    [Parameter(Mandatory)] [string] $Code,
    [string] $PublishDir = (Join-Path $PSScriptRoot '..\publish'),
    [string] $InstallDir = "$env:ProgramFiles\xKeep"
)
$ErrorActionPreference = 'Stop'
$ServiceName = 'xKeepAgent'
$ServiceExe = Join-Path $InstallDir 'XKeep.Agent.Service.exe'

# 1) 파일 복사 (Program Files 는 표준 사용자가 수정 불가 → 실행 파일 교체 우회 차단)
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { sc.exe stop $ServiceName | Out-Null; Start-Sleep 2 }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Copy-Item (Join-Path $PublishDir '*') $InstallDir -Recurse -Force

# 2) 페어링 (토큰은 %ProgramData%\xKeep 에 SYSTEM/관리자 전용으로 저장)
& $ServiceExe pair --server $Server --code $Code
if ($LASTEXITCODE -ne 0) { throw "페어링 실패" }

# 3) 서비스 등록: LocalSystem, 자동 시작
#    (cmd.exe 에서 수동 등록 시: sc.exe create xKeepAgent binPath= "\"C:\Program Files\xKeep\XKeep.Agent.Service.exe\"" start= auto obj= LocalSystem)
#    PowerShell 에서는 경로 따옴표 처리가 안전한 New-Service 사용 (기본 계정 = LocalSystem)
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName -BinaryPathName "`"$ServiceExe`"" -DisplayName 'xKeep Agent' -StartupType Automatic | Out-Null
}
sc.exe description $ServiceName "가족 기기 보호 — 사용 시간 관리 및 프로그램 제한" | Out-Null
# 비정상 종료 시 5초 후 자동 재시작 (무한 반복)
sc.exe failure $ServiceName reset= 0 actions= restart/5000/restart/5000/restart/5000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

# 4) 안전 모드에서도 서비스 실행 (안전 모드 부팅으로 제한을 우회하는 것 방지)
foreach ($mode in 'Minimal', 'Network') {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
    New-Item -Force $key | Out-Null
    Set-ItemProperty $key -Name '(default)' -Value 'Service'
}

sc.exe start $ServiceName | Out-Null
Get-Service $ServiceName | Format-Table Name, Status, StartType
Write-Host "설치 완료. 자녀 계정이 '표준 사용자'인지 반드시 확인하세요: net localgroup Administrators" -ForegroundColor Green
