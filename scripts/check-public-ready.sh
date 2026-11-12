#!/usr/bin/env bash
# 公开前脱敏自检。
#
# 用法：
#   bash scripts/check-public-ready.sh              # 扫工作区
#   bash scripts/check-public-ready.sh --history    # 连 git 历史一起扫
#
# 做这件事的理由：仓库一旦公开，即使马上改回私有，fork、缓存、搜索引擎里
# 也还留着。所以「先脱敏，再公开」不是流程，是唯一顺序。这个脚本只做检查，
# 不改任何文件 —— 命中了要人自己判断，自动替换容易把代码改坏。
#
# 检查两类东西：
#   ① 明显的凭据（令牌、私钥、云访问密钥）
#   ② 部署专属信息（自有域名、私有仓库路径、账号 id、真实校名）
# 第 ② 类不在 CI 的密钥扫描里，因为它长得不像密钥，但同样是"我的服务有关的信息"。

set -euo pipefail

cd "$(dirname "$0")/.."
export PYTHONIOENCODING=utf-8

HISTORY=0
[ "${1:-}" = "--history" ] && HISTORY=1

# 扫哪些文件：只扫"会进版本库"的那些。
#
# 这里刻意不用 grep -r：工作区里有 wrangler.deploy.toml、.dev.vars 这些
# 本机专属文件（已在 .gitignore 里），grep -r 会把它们一起扫进来，结果是
# **本地永远红、CI 永远绿** —— 那种"只有自己机器报错"的检查，跑两次就没人跑了。
# git ls-files -co --exclude-standard = 已跟踪 + 未跟踪但没被忽略的。
mapfile -t SCAN_FILES < <(
  git ls-files -z -co --exclude-standard \
    | tr '\0' '\n' \
    | grep -vE '(^|/)(node_modules|dist|_stale|shots|bin|obj|\.wrangler|\.workbuddy)/' \
    | grep -vE '\.(lock|png|jpe?g|gif|ico|zip|exe|dll|pdb|ttf|otf|woff2?|pdf|db|sqlite|snk|pfx)$' \
    | grep -v 'package-lock.json' || true
)
if [ ${#SCAN_FILES[@]} -eq 0 ]; then
  echo "❌ 一个文件都没扫到，确认当前目录是仓库根" >&2
  exit 1
fi

# ① 凭据格式。和 CI 的 secrets job 保持同一套，避免两边口径不一致。
CREDENTIALS='ghp_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|cfat_[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY-----|xox[baprs]-[A-Za-z0-9-]{10,}|glpat-[A-Za-z0-9_-]{20,}'

# ② 部署专属串。按自己的实际情况维护这一行 —— 换成你自己的域名、
#    组织名、账号 id。示例里全是占位符，正常应该一条都不命中。
#    默认只放几个通用形状（真实域名 / 私有库路径 / 长十六进制 id），
#    不预设任何具体的服务名，这样这份脚本本身也是干净的。
DEPLOY_SPECIFIC='(classpoints|sync|api)\.[a-z0-9-]+\.(top|eu\.org|com|cn)|[0-9a-f]{32}\.r2\.cloudflarestorage\.com|database_id *= *"[0-9a-f-]{36}"'

fail=0

echo "==> ① 明文凭据"
hits=$(grep -InE "$CREDENTIALS" "${SCAN_FILES[@]}" 2>/dev/null || true)
# 密钥扫描规则本身会命中自己（CI 里那行 patterns），排除掉
hits=$(printf '%s' "$hits" | grep -v "patterns=" | grep -v "CREDENTIALS=" | grep -v "check-public-ready.sh:" || true)
if [ -n "$hits" ]; then
  echo "$hits" | head -20
  echo "    ❌ 发现疑似凭据，先移除并轮换" >&2
  fail=1
else
  echo "    ✓ 无"
fi

echo "==> ② 部署专属信息（域名 / D1 id / 私有库路径）"
hits2=$(grep -InE "$DEPLOY_SPECIFIC" "${SCAN_FILES[@]}" 2>/dev/null || true)
# 白名单：文档/测试里刻意用的保留域名、占位 UUID、以及本脚本的规则本身。
# 这些是"看起来像真实值但其实不是"，不算泄露。
hits2=$(printf '%s' "$hits2" \
  | grep -v "check-public-ready.sh:" \
  | grep -viE 'example\.(com|org|net)|localhost|127\.0\.0\.1|YOUR_|<用户>|<仓库>|<用户名>' \
  | grep -v '00000000-0000-0000-0000-000000000000' || true)
if [ -n "$hits2" ]; then
  echo "$hits2" | head -20
  echo "    ❌ 发现部署专属信息，改成占位符或编译期注入" >&2
  fail=1
else
  echo "    ✓ 无"
fi

echo "==> ③ 不该进版本库的文件"
bad=0
for f in worker/.dev.vars .env .env.local .env.cnb.local sync.json session.bin; do
  if git ls-files --error-unmatch "$f" >/dev/null 2>&1; then
    echo "    ❌ $f 被提交进仓库了"
    bad=1
  fi
done
[ "$bad" = "0" ] && echo "    ✓ 无"

echo "==> ④ 更新地址是否只靠注入"
if printf '%s\n' "${SCAN_FILES[@]}" | grep -E '\.(cs|csproj|yml|iss|ps1)$' \
    | xargs -r grep -InE 'https://cnb\.cool/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+' 2>/dev/null \
    | grep -v '<' | grep -v 'someone/dist'; then
  echo "    ❌ 源码里写死了具体的版本库路径" >&2
  fail=1
else
  echo "    ✓ 源码里没有具体仓库路径（靠 vars.UPDATE_FEED 注入）"
fi

if [ "$HISTORY" = "1" ]; then
  echo "==> ⑤ git 历史"
  commits=$(git rev-list --all --count)
  echo "    提交数：$commits"
  allhits=$(git grep -nIE "$CREDENTIALS" $(git rev-list --all) -- . ':!*.lock' 2>/dev/null | head -10 || true)
  if [ -n "$allhits" ]; then
    echo "$allhits"
    echo "    ❌ 历史里仍有凭据，需要重建历史（删 .git 后重新 init 单根提交）" >&2
    fail=1
  else
    echo "    ✓ 历史里没有明文凭据"
  fi
fi

echo
if [ "$fail" != "0" ] || [ "$bad" != "0" ]; then
  echo "❌ 还不能公开。修完上面几条再跑一次。" >&2
  exit 1
fi

echo "✅ 检查通过。另外人工确认两件脚本查不出来的事："
echo "   - CNB 分发仓库的 Release 描述、附件里没有令牌或内网地址"
echo "   - 仓库里的示例数据用的是假校名、假人名"
echo "   - .workbuddy/ 等本地目录不会被整目录拷进公开仓库"
