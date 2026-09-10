@echo off
rem ============================================================
rem  run_whisper.bat - Windows wrapper for run_whisper.ps1
rem
rem  跳过 PowerShell ExecutionPolicy 限制, 直接调用 .ps1
rem  所有参数原样透传给脚本 (URL, -Model, -Lang, -OutDir 等)
rem
rem  用法:
rem    run_whisper "https://www.bilibili.com/video/BV1f3896bE7D/"
rem    run_whisper "D:\videos\foo.mp4" -Model ggml-base -Lang zh
rem    run_whisper "URL" -KeepIntermediate
rem ============================================================

setlocal

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%run_whisper.ps1"

if not exist "%PS_SCRIPT%" (
    echo [ERROR] run_whisper.ps1 not found in "%SCRIPT_DIR%"
    echo         Make sure this .bat is in the same folder as the .ps1
    exit /b 1
)

where powershell.exe >nul 2>&1
if errorlevel 1 (
    echo [ERROR] powershell.exe not in PATH
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
exit /b %ERRORLEVEL%
