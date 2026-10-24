<#
  一键准备 Windows 运行环境：下载 stable-diffusion.cpp 官方包 + 4 个模型文件。
  用法：
    powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
    powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Backend vulkan
    powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -SkipModels
  可选 Backend：cuda12（默认，NVIDIA）、vulkan（A 卡/核显/通用）、cpu（仅 CPU）
#>
param(
    [ValidateSet('cuda12', 'vulkan', 'cpu')]
    [string]$Backend = 'cuda12',
    [switch]$SkipModels,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
New-Item -ItemType Directory -Force -Path downloads, bin, models\text_encoders, models\vae, models\tokenizers | Out-Null

if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) {
    throw '需要 curl.exe（Windows 10 1803 及以上自带）。'
}

function Download([string]$Url, [string]$Out, [long]$MinBytes = 1000000) {
    if ((Test-Path $Out) -and -not $Force) {
        $len = (Get-Item $Out).Length
        if ($len -ge $MinBytes) { Write-Host "已存在，跳过: $Out"; return }
    }
    Write-Host "下载 $Url"
    & curl.exe -L --fail --retry 3 --retry-delay 3 --connect-timeout 30 -C - -o $Out $Url
    if ($LASTEXITCODE -ne 0) { throw "下载失败: $Url" }
}

function Expand-Zip([string]$Zip, [string]$Dest) {
    Write-Host "解压 $Zip -> $Dest"
    if (Get-Command tar.exe -ErrorAction SilentlyContinue) {
        & tar.exe -xf $Zip -C $Dest
        if ($LASTEXITCODE -eq 0) { return }
    }
    Expand-Archive -LiteralPath $Zip -DestinationPath $Dest -Force
}

Write-Host '查询 stable-diffusion.cpp 最新版本...'
$headers = @{ 'User-Agent' = 'moss-image-studio-setup' }
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest' -Headers $headers

$pattern = switch ($Backend) {
    'cuda12' { 'bin-win-cuda12-x64\.zip$' }
    'vulkan' { 'bin-win-vulkan-x64\.zip$' }
    'cpu'    { 'bin-win-cpu-x64\.zip$' }
}
$asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
if (-not $asset) {
    throw "版本 $($release.tag_name) 里没有 $Backend 的 Windows 包。可选: $(($release.assets | ForEach-Object name) -join ', ')"
}

$zip = Join-Path 'downloads' $asset.name
Download $asset.browser_download_url $zip ([long]($asset.size * 0.98))
Expand-Zip $zip 'bin'

if ($Backend -eq 'cuda12' -and -not (Test-Path 'bin\cublasLt64_12.dll')) {
    $cudart = $release.assets | Where-Object { $_.name -match 'cudart.*win-cu12.*\.zip$' } | Select-Object -First 1
    if ($cudart) {
        $czip = Join-Path 'downloads' $cudart.name
        Download $cudart.browser_download_url $czip ([long]($cudart.size * 0.98))
        Expand-Zip $czip 'bin'
    }
}

if (-not $SkipModels) {
    $models = @(
        @{ Url = 'https://huggingface.co/leejet/MOSS-Image-SFT-GGUF/resolve/main/MOSS-Image-SFT-Q4_K.gguf'; Out = 'models\MOSS-Image-SFT-Q4_K.gguf'; MinMB = 3000 },
        @{ Url = 'https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf'; Out = 'models\text_encoders\Qwen3.5-9B-Q4_K_M.gguf'; MinMB = 5000 },
        @{ Url = 'https://huggingface.co/Comfy-Org/MOSS-Image/resolve/main/vae/flux2-vae.safetensors'; Out = 'models\vae\flux2-vae.safetensors'; MinMB = 300 },
        @{ Url = 'https://huggingface.co/OpenMOSS-Team/MOSS-Image-Base/resolve/main/tokenizer/tokenizer.json'; Out = 'models\tokenizers\tokenizer.json'; MinMB = 10 }
    )
    Write-Host ''
    Write-Host '模型共约 9.4 GB，可断点续传；中断后重新运行本脚本即可继续。'
    foreach ($m in $models) {
        Download $m.Url $m.Out ([long]($m.MinMB * 1MB * 0.98))
        $size = [math]::Round((Get-Item $m.Out).Length / 1MB, 1)
        if ($size -lt $m.MinMB) {
            Write-Warning "$($m.Out) 只有 $size MB，可能下载不完整或被拒绝；请删除后重试。"
        }
    }
}

if (-not (Test-Path 'moss.env')) {
    Copy-Item 'moss.env.example' 'moss.env'
    Write-Host '已生成 moss.env（默认配置）。'
}

Write-Host ''
Write-Host '准备完成。'
Write-Host '  双击 MOSS-Image.exe 启动；想先自查环境可运行 MOSS-Image.exe --check'
Write-Host '  手机/平板访问：把 moss.env 里的 MOSS_LISTEN 改成 0.0.0.0'
