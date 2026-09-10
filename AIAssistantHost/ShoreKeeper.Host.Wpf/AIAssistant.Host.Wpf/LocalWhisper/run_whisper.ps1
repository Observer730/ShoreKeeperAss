<#
.SYNOPSIS
  One-shot B站/本地视频 -> SRT 字幕 (whisper.cpp 路线)

.DESCRIPTION
  流程:  下载音频 (B站)  ->  转 16k mono wav  ->  whisper-cli 转写  ->  SRT

  路径默认硬编码到本机, 可通过环境变量覆盖:
    $env:WHISPER_YTDLP    = "C:\path\to\yt-dlp.exe"
    $env:WHISPER_FFMPEG   = "C:\path\to\ffmpeg.exe"
    $env:WHISPER_CLI      = "C:\path\to\whisper-cli.exe"
    $env:WHISPER_MODELS   = "C:\path\to\models"

.PARAMETER Input
  B 站 URL (https://www.bilibili.com/video/BVxxx) 或 本地媒体文件路径

.PARAMETER OutDir
  输出目录. 默认: URL -> 当前目录; 本地文件 -> 所在目录

.PARAMETER Model
  模型名 (不含 .bin). 可选: ggml-tiny / ggml-base / ggml-small / ggml-medium / ggml-large-v3
  默认: ggml-medium

.PARAMETER Lang
  语言. auto / zh / en / ...  默认: auto

.PARAMETER KeepIntermediate
  保留中间 wav 文件 (默认转写完删掉)

.EXAMPLE
  .\run_whisper.ps1 "https://www.bilibili.com/video/BV1f3896bE7D/"
  .\run_whisper.ps1 "D:\videos\foo.mp4" -Model ggml-base -Lang zh
  .\run_whisper.ps1 "BV1f3896bE7D.wav" -KeepIntermediate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Alias('Input', 'i')]
    [string]$OutDir,
    [string]$Model = "ggml-medium",
    [string]$Lang  = "auto",
    [switch]$KeepIntermediate
)

$ErrorActionPreference = 'Stop'

# ----- 路径解析 (env var 优先) -----
function Resolve-ToolPath([string]$envName, [string]$fallback) {
    $value = [Environment]::GetEnvironmentVariable($envName)
    if ([string]::IsNullOrWhiteSpace($value)) { return $fallback }
    return $value
}

$YtDlp      = Resolve-ToolPath 'WHISPER_YTDLP'  'C:\Users\ASUS\AppData\Local\Packages\PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0\LocalCache\local-packages\Python312\Scripts\yt-dlp.exe'
$Ffmpeg     = Resolve-ToolPath 'WHISPER_FFMPEG' 'C:\Users\ASUS\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe'
$WhisperExe = Resolve-ToolPath 'WHISPER_CLI'    'D:\Project\ShoreKeeperAss\AIAssistantHost\ShoreKeeper.Host.Wpf\AIAssistant.Host.Wpf\LocalWhisper\whisper-cli.exe'
$ModelDir   = Resolve-ToolPath 'WHISPER_MODELS' 'D:\Project\ShoreKeeperAss\AIAssistantHost\ShoreKeeper.Host.Wpf\AIAssistant.Host.Wpf\LocalWhisper\models'

# ----- 前置检查 -----
$missing = @()
foreach ($pair in @(
    @{ n = 'yt-dlp';     p = $YtDlp },
    @{ n = 'ffmpeg';     p = $Ffmpeg },
    @{ n = 'whisper-cli';p = $WhisperExe },
    @{ n = 'models dir'; p = $ModelDir }
)) {
    if (-not (Test-Path $pair.p)) { $missing += "$($pair.n) -> $($pair.p)" }
}
if ($missing.Count -gt 0) {
    Write-Host "ERROR: 下列路径找不到:" -ForegroundColor Red
    $missing | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "可设置环境变量覆盖, 例如:" -ForegroundColor Yellow
    Write-Host '  $env:WHISPER_FFMPEG = "C:\other\ffmpeg.exe"'
    exit 1
}

$ModelPath = Join-Path $ModelDir "$Model.bin"
if (-not (Test-Path $ModelPath)) {
    $available = (Get-ChildItem $ModelDir -Filter 'ggml-*.bin' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name) -join ', '
    Write-Host "ERROR: model not found: $ModelPath" -ForegroundColor Red
    Write-Host "Available: $available" -ForegroundColor Yellow
    exit 1
}

# ----- 输入判定 -----
$isUrl = $InputPath -match '^https?://'
if (-not $isUrl -and -not (Test-Path $InputPath)) {
    Write-Host "ERROR: input not found: $InputPath" -ForegroundColor Red
    exit 1
}

# ----- 输出目录 -----
if ($OutDir) {
    if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
    $workDir = (Resolve-Path $OutDir).Path
} elseif ($isUrl) {
    $workDir = (Get-Location).Path
} else {
    $workDir = (Split-Path (Resolve-Path $InputPath) -Parent).Path
}
Set-Location $workDir

# ----- 基名 (B 站用 BV 号, 本地用文件名) -----
if ($isUrl) {
    if ($InputPath -match 'BV[0-9A-Za-z]+') { $baseName = $Matches[0] }
    else { $baseName = "input_$([DateTimeOffset]::Now.ToUnixTimeSeconds())" }
} else {
    $baseName = [System.IO.Path]::GetFileNameWithoutExtension($InputPath)
}

Write-Host ""
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host " run_whisper" -ForegroundColor Cyan
Write-Host "  input   : $InputPath" -ForegroundColor DarkGray
Write-Host "  workdir : $workDir" -ForegroundColor DarkGray
Write-Host "  model   : $Model" -ForegroundColor DarkGray
Write-Host "  lang    : $Lang" -ForegroundColor DarkGray
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host ""

# ----- Step 1: 获取音频 -----
$m4aPath = Join-Path $workDir "$baseName.m4a"
if ($isUrl) {
    Write-Host "[1/4] download audio from B站 ..." -ForegroundColor Green
    # 匿名 m4a 体积小, 失败再回退到 bestaudio
    & $YtDlp -f "bestaudio[ext=m4a]/bestaudio" --ffmpeg-location $Ffmpeg `
        -o "$baseName.%(ext)s" --no-warnings --no-progress $InputPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  yt-dlp failed, trying ffmpeg direct fallback ..." -ForegroundColor Yellow
        & $YtDlp -f "bestaudio" --ffmpeg-location $Ffmpeg `
            -o "$baseName.%(ext)s" --no-warnings --no-progress $InputPath
        if ($LASTEXITCODE -ne 0) { throw "yt-dlp failed" }
    }
    # 找到下载下来的音频
    $candidates = @('m4a','mp3','wav','ogg','flac','webm','opus','mkv','mp4')
    $downloaded = $null
    foreach ($ext in $candidates) {
        $p = Join-Path $workDir "$baseName.$ext"
        if (Test-Path -LiteralPath $p) { $downloaded = Get-Item -LiteralPath $p; break }
    }
    if (-not $downloaded) {
        # last resort: any new file with audio extension
        $downloaded = Get-ChildItem -LiteralPath $workDir -File |
            Where-Object { $_.Extension -in '.m4a','.mp3','.wav','.ogg','.flac','.webm','.opus','.mkv','.mp4' } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }
    if (-not $downloaded) { throw "yt-dlp did not produce an audio file in $workDir" }
    $m4aPath = $downloaded.FullName
    Write-Host ("  -> {0} ({1:N1} MB)" -f $downloaded.Name, ($downloaded.Length/1MB)) -ForegroundColor DarkGray
} else {
    $m4aPath = (Resolve-Path $InputPath).Path
    Write-Host "[1/4] using local file: $m4aPath" -ForegroundColor Green
}

# ----- Step 2: 转 wav -----
$wavPath = Join-Path $workDir "$baseName.wav"
$srcExt  = [System.IO.Path]::GetExtension($m4aPath).ToLower()
if ($srcExt -eq '.wav' -and -not $isUrl) {
    $wavPath = $m4aPath
    Write-Host "[2/4] already wav, skipping conversion" -ForegroundColor Green
} else {
    Write-Host "[2/4] ffmpeg -> 16k mono wav ..." -ForegroundColor Green
    # Use Start-Process to keep ffmpeg's stderr out of our stream (avoids noise in test runners)
    $ffArgs = @('-y', '-i', $m4aPath, '-ar', '16000', '-ac', '1', '-c:a', 'pcm_s16le', $wavPath)
    $ffProc = Start-Process -FilePath $Ffmpeg -ArgumentList $ffArgs -NoNewWindow -Wait -PassThru -RedirectStandardError "$workDir\.ffmpeg.err.log"
    if ($ffProc.ExitCode -ne 0) {
        Write-Host "  ffmpeg stderr:" -ForegroundColor Yellow
        Get-Content "$workDir\.ffmpeg.err.log" -Tail 10 | ForEach-Object { Write-Host "    $_" }
        Remove-Item "$workDir\.ffmpeg.err.log" -ErrorAction SilentlyContinue
        throw "ffmpeg failed (exit $($ffProc.ExitCode))"
    }
    Remove-Item "$workDir\.ffmpeg.err.log" -ErrorAction SilentlyContinue
    $wavSize = (Get-Item $wavPath).Length
    Write-Host ("  -> {0}.wav ({1:N1} MB)" -f $baseName, ($wavSize/1MB)) -ForegroundColor DarkGray
}

# ----- Step 3: whisper-cli 转写 -----
Write-Host "[3/4] whisper-cli ($Model, lang=$Lang) ..." -ForegroundColor Green
$sw = [System.Diagnostics.Stopwatch]::StartNew()
& $WhisperExe -m $ModelPath -l $Lang -f $wavPath -osrt -of $baseName
$sw.Stop()
if ($LASTEXITCODE -ne 0) { throw "whisper-cli failed" }
Write-Host ("  -> done in {0:mm\:ss}" -f $sw.Elapsed) -ForegroundColor DarkGray

# ----- Step 4: 清理 + 收尾 -----
$srtPath = Join-Path $workDir "$baseName.srt"
if (-not $KeepIntermediate) {
    if ($isUrl -and (Test-Path $m4aPath) -and $m4aPath -ne $wavPath) { Remove-Item $m4aPath -Force }
    if ($wavPath -ne $m4aPath -and (Test-Path $wavPath))              { Remove-Item $wavPath -Force }
    Write-Host "[4/4] cleaned up intermediate files" -ForegroundColor Green
} else {
    Write-Host "[4/4] kept intermediate files (KeepIntermediate)" -ForegroundColor Green
}

Write-Host ""
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host " DONE" -ForegroundColor Cyan
Write-Host "  SRT: $srtPath" -ForegroundColor White
if (Test-Path $srtPath) {
    $lines = (Get-Content $srtPath).Count
    $bytes = (Get-Item $srtPath).Length
    Write-Host "  ($lines lines, $("{0:N1}" -f ($bytes/1KB)) KB)" -ForegroundColor DarkGray
}
Write-Host "===========================================" -ForegroundColor Cyan
Write-Host ""
