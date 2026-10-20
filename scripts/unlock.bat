@echo off
setlocal
rem PCL2 下载功能解锁补丁 —— Windows 一键脚本（双击运行，或从命令行调用）
rem
rem 用法：
rem   unlock.bat                              自动在当前目录 / 项目根目录找 PCL2 的 exe
rem   unlock.bat "D:\PCL2\Plain Craft Launcher 2.exe"
rem   unlock.bat "D:\PCL2\PCL2.exe" -Only 1    只补「本体下载」
rem   unlock.bat "D:\PCL2\PCL2.exe" -Check     只检查是否已解锁

set "PS1=%~dp0unlock.ps1"
if not exist "%PS1%" (
    echo 找不到 unlock.ps1，请确认脚本目录完整。
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "CODE=%ERRORLEVEL%"

rem 双击运行时窗口会一闪而过，暂停一下方便看结果
echo %cmdcmdline% | find /i "%~nx0" >nul && pause

exit /b %CODE%
