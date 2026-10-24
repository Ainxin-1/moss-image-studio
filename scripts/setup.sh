#!/usr/bin/env sh
# MOSS-Image Studio —— Linux / macOS 环境准备
# 用法:
#   ./scripts/setup.sh                    # 自动选择后端（Linux 默认 CPU 版，macOS arm64）
#   ./scripts/setup.sh --backend=vulkan   # Linux: cpu | vulkan | rocm
#   ./scripts/setup.sh --skip-models
set -eu

BACKEND=auto
SKIP_MODELS=0
for arg in "$@"; do
    case "$arg" in
        --backend=*) BACKEND=${arg#*=} ;;
        --skip-models) SKIP_MODELS=1 ;;
        -h|--help)
            printf '用法: %s [--backend=cpu|vulkan|rocm] [--skip-models]\n' "$0"
            exit 0
            ;;
        *) printf '未知参数: %s\n' "$arg" >&2; exit 2 ;;
    esac
done

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$ROOT"
mkdir -p downloads bin models/text_encoders models/vae models/tokenizers

command -v curl >/dev/null 2>&1 || { printf '需要 curl。\n' >&2; exit 1; }
command -v unzip >/dev/null 2>&1 || { printf '需要 unzip（Debian/Ubuntu: sudo apt install unzip）。\n' >&2; exit 1; }

os=$(uname -s)
arch=$(uname -m)

case "$os" in
    Darwin)
        [ "$arch" = "arm64" ] || { printf '官方只提供 macOS arm64 包（当前 %s）。\n' "$arch" >&2; exit 1; }
        pattern='Darwin-macOS.*arm64\.zip$'
        ;;
    Linux)
        [ "$arch" = "x86_64" ] || { printf '官方只提供 Linux x86_64 包（当前 %s）。\n' "$arch" >&2; exit 1; }
        case "$BACKEND" in
            auto|cpu) pattern='Linux-Ubuntu-24\.04-x86_64\.zip$' ;;
            vulkan)   pattern='Linux-Ubuntu-24\.04-x86_64-vulkan\.zip$' ;;
            rocm)     pattern='Linux-Ubuntu-24\.04-x86_64-rocm.*\.zip$' ;;
            *) printf '未知后端: %s（可选 cpu / vulkan / rocm）\n' "$BACKEND" >&2; exit 2 ;;
        esac
        ;;
    *)
        printf '不支持的系统: %s。Windows 请用 scripts\\setup.ps1。\n' "$os" >&2
        exit 1
        ;;
esac

printf '查询 stable-diffusion.cpp 最新版本...\n'
json=$(curl -fsSL -H 'User-Agent: moss-image-studio-setup' \
    https://api.github.com/repos/leejet/stable-diffusion.cpp/releases/latest)
url=$(printf '%s' "$json" | tr ',' '\n' \
    | grep -o '"browser_download_url": *"[^"]*"' \
    | sed 's/.*"\(https[^"]*\)"/\1/' \
    | grep -E "$pattern" | head -1)

if [ -z "$url" ]; then
    printf '没有找到匹配 %s 的包，发布页: https://github.com/leejet/stable-diffusion.cpp/releases/latest\n' "$pattern" >&2
    exit 1
fi

zip="downloads/$(basename "$url")"
if [ ! -f "$zip" ]; then
    printf '下载 %s\n' "$url"
    curl -L --fail --retry 3 --retry-delay 3 --connect-timeout 30 -C - -o "$zip" "$url"
else
    printf '已存在，跳过: %s\n' "$zip"
fi

printf '解压到 bin/ ...\n'
unzip -o -q "$zip" -d bin
chmod +x bin/sd-server 2>/dev/null || true
if [ "$os" = "Darwin" ]; then
    xattr -dr com.apple.quarantine bin 2>/dev/null || true
fi

fetch_model() {
    out=$1
    link=$2
    min=$3
    if [ -f "$out" ]; then
        size=$(wc -c < "$out" | tr -d ' ')
        if [ "$size" -ge "$min" ]; then
            printf '已存在，跳过: %s\n' "$out"
            return 0
        fi
    fi
    printf '下载 %s\n' "$out"
    if ! curl -L --fail --retry 3 --retry-delay 3 --connect-timeout 30 -C - -o "$out" "$link"; then
        printf '下载失败: %s\n可手动下载后放到 %s\n' "$link" "$out" >&2
    fi
}

if [ "$SKIP_MODELS" -eq 0 ]; then
    printf '\n模型共约 9.4 GB，可断点续传；中断后重跑本脚本即可继续。\n'
    fetch_model models/MOSS-Image-SFT-Q4_K.gguf \
        https://huggingface.co/leejet/MOSS-Image-SFT-GGUF/resolve/main/MOSS-Image-SFT-Q4_K.gguf \
        3000000000
    fetch_model models/text_encoders/Qwen3.5-9B-Q4_K_M.gguf \
        https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/resolve/main/Qwen3.5-9B-Q4_K_M.gguf \
        5000000000
    fetch_model models/vae/flux2-vae.safetensors \
        https://huggingface.co/Comfy-Org/MOSS-Image/resolve/main/vae/flux2-vae.safetensors \
        300000000
    fetch_model models/tokenizers/tokenizer.json \
        https://huggingface.co/OpenMOSS-Team/MOSS-Image-Base/resolve/main/tokenizer/tokenizer.json \
        10000000
fi

if [ ! -f moss.env ]; then
    cp moss.env.example moss.env
    printf '已生成 moss.env（默认配置）。\n'
fi

printf '\n准备完成。运行 ./run.sh 启动。\n'
