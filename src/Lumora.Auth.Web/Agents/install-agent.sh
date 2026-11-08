#!/usr/bin/env bash
# Lumora Agent installer for Linux (systemd). The site fills in its address.
# Usage: curl -fsSL __SITE_URL__/agent/install.sh | sudo bash -s -- --token <token> [--name <name>] [--version v0.2.0]
set -euo pipefail

SITE_URL="__SITE_URL__"
REPO="__AGENT_REPO__"
TOKEN=""
NAME=""
VERSION="latest"
while [ $# -gt 0 ]; do
  case "$1" in
    --token) TOKEN="${2:-}"; shift 2 ;;
    --name) NAME="${2:-}"; shift 2 ;;
    --version) VERSION="${2:-}"; shift 2 ;;
    *) echo "Неизвестный параметр: $1" >&2; exit 1 ;;
  esac
done

[ -n "$TOKEN" ] || { echo "Нужен --token: возьмите команду в админке (Агенты → Добавить агента)." >&2; exit 1; }
[ "$(id -u)" = 0 ] || { echo "Запустите через sudo." >&2; exit 1; }
command -v systemctl >/dev/null || { echo "Нужен systemd." >&2; exit 1; }
for tool in curl tar sha256sum; do
  command -v "$tool" >/dev/null || { echo "Не найдена программа $tool." >&2; exit 1; }
done

case "$(uname -m)" in
  x86_64|amd64) RID=linux-x64 ;;
  aarch64|arm64) RID=linux-arm64 ;;
  *) echo "Архитектура $(uname -m) не поддерживается." >&2; exit 1 ;;
esac

ASSET="lumora-agent-$RID.tar.gz"
if [ "$VERSION" = latest ]; then
  BASE="https://github.com/$REPO/releases/latest/download"
else
  BASE="https://github.com/$REPO/releases/download/$VERSION"
fi

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
echo "Скачиваю агента ($RID, $VERSION)…"
curl -fsSL -o "$TMP/$ASSET" "$BASE/$ASSET"
curl -fsSL -o "$TMP/checksums.txt" "$BASE/checksums.txt"
(cd "$TMP" && grep " $ASSET\$" checksums.txt | sha256sum -c --quiet -) || { echo "Контрольная сумма не совпала." >&2; exit 1; }

id lumora >/dev/null 2>&1 || useradd --system --home-dir /var/lib/lumora-agent --shell /usr/sbin/nologin lumora
systemctl stop lumora-agent 2>/dev/null || true
install -d -m 755 /opt/lumora-agent
tar -xzf "$TMP/$ASSET" -C /opt/lumora-agent
chmod 755 /opt/lumora-agent/lumora-agent
install -d -o lumora -g lumora -m 750 /var/lib/lumora-agent
install -d -o root -g lumora -m 750 /etc/lumora-agent

echo "Регистрирую агента на $SITE_URL…"
/opt/lumora-agent/lumora-agent register --url "$SITE_URL" --token "$TOKEN" ${NAME:+--name "$NAME"} \
  --config /etc/lumora-agent/agent.json --data /var/lib/lumora-agent
chown root:lumora /etc/lumora-agent/agent.json
chmod 640 /etc/lumora-agent/agent.json

cat > /etc/systemd/system/lumora-agent.service <<'UNIT'
[Unit]
Description=Lumora Agent (game servers)
After=network-online.target
Wants=network-online.target

[Service]
User=lumora
Group=lumora
ExecStart=/opt/lumora-agent/lumora-agent run --config /etc/lumora-agent/agent.json
Restart=always
RestartSec=5
# The agent stops game servers itself (save, "stop", wait) when systemd stops it.
KillMode=mixed
TimeoutStopSec=180
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
PrivateTmp=true
ReadWritePaths=/var/lib/lumora-agent
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable --now lumora-agent
echo "Готово: агент запущен и появится в админке через несколько секунд."
echo "Журнал: journalctl -u lumora-agent -f"
