#!/usr/bin/env sh
# MOSS-Image Studio —— Linux / macOS 启动脚本
# 用法: ./run.sh      （Ctrl+C 停止服务）
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$ROOT"

if [ -f moss.env ]; then
    set -a
    # shellcheck disable=SC1091
    . ./moss.env
    set +a
fi

# 去掉 Windows 换行带来的 \r
strip() { printf '%s' "$1" | tr -d '\r'; }

PORT=$(strip "${MOSS_PORT:-1234}")
LISTEN=$(strip "${MOSS_LISTEN:-127.0.0.1}")
MODEL_DIR=$(strip "${MOSS_MODEL_DIR:-models}")
DIFFUSION=$(strip "${MOSS_DIFFUSION_MODEL:-models/MOSS-Image-SFT-Q4_K.gguf}")
TEXT_ENCODER=$(strip "${MOSS_TEXT_ENCODER:-models/text_encoders/Qwen3.5-9B-Q4_K_M.gguf}")
VAE=$(strip "${MOSS_VAE:-models/vae/flux2-vae.safetensors}")
TOKENIZER=$(strip "${MOSS_TOKENIZER:-models/tokenizers/tokenizer.json}")
HTML=$(strip "${MOSS_HTML:-web/index.html}")
OFFLOAD=$(strip "${MOSS_OFFLOAD_TO_CPU:-1}")
EXTRA=$(strip "${MOSS_EXTRA_ARGS:-}")
SERVER=$(strip "${MOSS_SERVER:-bin/sd-server}")

[ -x "$SERVER" ] || { [ -f "$SERVER" ] && chmod +x "$SERVER" 2>/dev/null; }
if [ ! -f "$SERVER" ]; then
    printf '找不到 sd-server: %s\n先运行 ./scripts/setup.sh，或把官方 Linux/macOS 包解压到 bin/。\n' "$SERVER" >&2
    exit 1
fi

missing=0
for f in "$HTML" "$DIFFUSION" "$TEXT_ENCODER" "$VAE" "$TOKENIZER"; do
    if [ ! -f "$f" ]; then
        printf '缺少文件: %s\n' "$f" >&2
        missing=1
    fi
done
if [ ! -d "$MODEL_DIR" ]; then
    printf '缺少目录: %s\n' "$MODEL_DIR" >&2
    missing=1
fi
if [ "$missing" -ne 0 ]; then
    printf '\n把文件放到上面的位置，或在 moss.env 里指向实际路径。\n' >&2
    exit 1
fi

URL="http://127.0.0.1:$PORT/"

is_up() {
    command -v curl >/dev/null 2>&1 || return 1
    curl -fsS -o /dev/null --max-time 2 "$URL"
}

lan_ip() {
    ip=$(hostname -I 2>/dev/null | awk '{print $1}') || ip=""
    [ -n "${ip:-}" ] || ip=$(ipconfig getifaddr en0 2>/dev/null) || ip=""
    [ -n "${ip:-}" ] || ip=$(ipconfig getifaddr en1 2>/dev/null) || ip=""
    printf '%s' "${ip:-<本机IP>}"
}

open_ui() {
    if [ "$LISTEN" != "127.0.0.1" ] && [ "$LISTEN" != "localhost" ] && [ "$LISTEN" != "::1" ]; then
        printf '局域网/手机访问: http://%s:%s\n' "$(lan_ip)" "$PORT"
    fi
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open "$1" >/dev/null 2>&1 &
    elif command -v open >/dev/null 2>&1; then
        open "$1" >/dev/null 2>&1 &
    else
        printf '用浏览器打开: %s\n' "$1"
    fi
}

if is_up; then
    printf '服务已在运行: %s\n' "$URL"
    open_ui "$URL"
    exit 0
fi

set -- --listen-ip "$LISTEN" --listen-port "$PORT" \
       --serve-html-path "$HTML" --model-dir "$MODEL_DIR" \
       --diffusion-model "$DIFFUSION" --llm "$TEXT_ENCODER" \
       --vae "$VAE" --tokenizer "$TOKENIZER"
case "$OFFLOAD" in
    1|true|yes|on) set -- "$@" --offload-to-cpu ;;
esac
if [ -n "$EXTRA" ]; then
    # 允许 MOSS_EXTRA_ARGS 里写多个参数；含空格的值请自行用引号包好
    # shellcheck disable=SC2086
    set -- "$@" $EXTRA
fi

"$SERVER" "$@" &
PID=$!
trap 'kill "$PID" 2>/dev/null || true' INT TERM EXIT

if ! command -v curl >/dev/null 2>&1; then
    printf '未安装 curl，等待 10 秒后直接打开界面。\n'
    sleep 10
    open_ui "$URL"
    wait "$PID"
    exit 0
fi

printf '正在加载模型（30~90 秒）...\n'
i=0
while [ "$i" -lt 300 ]; do
    if ! kill -0 "$PID" 2>/dev/null; then
        wait "$PID" || true
        printf '服务已退出，请检查上面的日志。\n' >&2
        exit 1
    fi
    if is_up; then
        printf '就绪: %s\n' "$URL"
        open_ui "$URL"
        wait "$PID"
        exit 0
    fi
    sleep 1
    i=$((i + 1))
done

printf '等待超时，服务仍在运行（PID %s）。\n' "$PID" >&2
wait "$PID"
