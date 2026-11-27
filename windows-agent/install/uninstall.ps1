#Requires -RunAsAdministrator
# xKeep Windows 에이전트 제거 (관리자 PowerShell). 부모만 실행 가능.
param([switch] $KeepData)
$ErrorActionPreference = 'Continue'
$ServiceName = 'xKeepAgent'

sc.exe stop $ServiceName | Out-Null
Start-Sleep 2
Get-Process XKeep.Agent.UserHost -ErrorAction SilentlyContinue | Stop-Process -Force
sc.exe delete $ServiceName | Out-Null
foreach ($mode in 'Minimal', 'Network') {
    Remove-Item -Force -ErrorAction SilentlyContinue "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
}
# 학습 모드 중에 제거하면 남아 있을 수 있는 브라우저 사이트 제한 정책 정리
foreach ($key in 'HKLM:\SOFTWARE\Policies\Google\Chrome', 'HKLM:\SOFTWARE\Policies\Microsoft\Edge') {
    if (Test-Path $key) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$key\URLBlocklist", "$key\URLAllowlist"
        Remove-ItemProperty -Path $key -Name IncognitoModeAvailability, InPrivateModeAvailability -ErrorAction SilentlyContinue
    }
}
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$env:ProgramFiles\xKeep"
if (-not $KeepData) { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "$env:ProgramData\xKeep" }
Write-Host "제거 완료"
