#!/usr/bin/env bash
# PCL2 下载功能解锁补丁 —— 一键脚本（macOS / Linux / WSL / Git Bash）
#
# 用法：
#   scripts/unlock.sh                       # 自动在当前目录 / 项目根目录找 PCL2 的 exe
#   scripts/unlock.sh "/path/to/PCL2.exe"    # 指定输入文件
#   scripts/unlock.sh PCL2.exe -o out.exe    # 其余参数原样透传给补丁工具
#   scripts/unlock.sh PCL2.exe --only 1      # 只补本体下载
#
# 需要：.NET SDK 8 或更高版本（dnlib 由 NuGet 自动还原）。

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/PCL2Unlock"

die() { printf '错误：%s\n' "$*" >&2; exit 1; }
info() { printf '› %s\n' "$*"; }

command -v dotnet >/dev/null 2>&1 \
    || die "未找到 dotnet，请先安装 .NET SDK 8 或更高版本：https://dotnet.microsoft.com/download"

# 1. 定位输入文件
INPUT="${1:-}"
if [[ -z "$INPUT" ]]; then
    for candidate in "Plain Craft Launcher 2.exe" "PCL2.exe" "$ROOT/Plain Craft Launcher 2.exe" "$ROOT/PCL2.exe"; do
        if [[ -f "$candidate" ]]; then
            INPUT="$candidate"
            break
        fi
    done
fi
[[ -n "$INPUT" && -f "$INPUT" ]] \
    || die "找不到 PCL2 的 exe，请把路径作为第一个参数传入，例如：scripts/unlock.sh \"Plain Craft Launcher 2.exe\""
shift || true

# 2. 用户在额外参数里指定了 -o 就用他的，否则输出到 dist/
USER_OUTPUT=""
args=("$@")
for ((i = 0; i < ${#args[@]}; i++)); do
    case "${args[i]}" in
        -o|--output) USER_OUTPUT="${args[i + 1]:-}" ;;
    esac
done

if [[ -n "$USER_OUTPUT" ]]; then
    OUTPUT="$USER_OUTPUT"
else
    mkdir -p "$ROOT/dist"
    OUTPUT="$ROOT/dist/$(basename "${INPUT%.exe}").unlocked.exe"
    args+=(-o "$OUTPUT")
fi

info "输入：$INPUT"
info "构建补丁工具"
dotnet build "$PROJECT" -v q --nologo

info "打补丁"
dotnet run --project "$PROJECT" --no-build -- "$INPUT" "${args[@]}"

# 3. 输出存在就校验一遍
if [[ -f "$OUTPUT" ]]; then
    info "校验输出文件"
    dotnet run --project "$PROJECT" --no-build -- "$OUTPUT" --check || true

    if command -v shasum >/dev/null 2>&1; then
        printf '\nSHA-256：%s\n' "$(shasum -a 256 "$OUTPUT" | awk '{print $1}')"
    elif command -v sha256sum >/dev/null 2>&1; then
        printf '\nSHA-256：%s\n' "$(sha256sum "$OUTPUT" | awk '{print $1}')"
    fi

    printf '输出文件：%s\n' "$OUTPUT"
    printf '\n完成。用解锁后的 exe 替换原文件即可（建议先备份原 exe）。\n'
fi
