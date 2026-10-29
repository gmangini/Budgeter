#!/bin/sh
# Runs on the server, from the unpacked deploy bundle. Idempotent: safe to re-run on every deploy.
set -eu
if [ "$(id -u)" -ne 0 ]; then exec sudo sh "$0" "$@"; fi
here="$(cd "$(dirname "$0")" && pwd)"

# .NET needs ICU for culture data (the app formats dollars as en-US); minimal images leave it out.
if ! ldconfig -p | grep -q libicuuc; then
  echo "Installing ICU..."
  apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libicu-dev >/dev/null
fi
# Charts need a font file to draw text with; the app looks for DejaVu Sans.
if [ ! -f /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf ]; then
  echo "Installing fonts for charts..."
  apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq fonts-dejavu-core >/dev/null
fi

id budgeter >/dev/null 2>&1 || useradd --system --home-dir /opt/budgeter --shell /usr/sbin/nologin budgeter
mkdir -p /opt/budgeter/app /opt/budgeter/data

# New app files replace the old ones; .env and data/ are only copied when this bundle has them (first deploy).
systemctl stop budgeter 2>/dev/null || true
rm -rf /opt/budgeter/app/*
cp -R "$here/app/." /opt/budgeter/app/
chmod 755 /opt/budgeter/app/Budgeter
if [ -f "$here/.env" ]; then
  install -m 600 -o budgeter -g budgeter "$here/.env" /opt/budgeter/.env
fi
if [ -d "$here/data" ]; then
  cp -R "$here/data/." /opt/budgeter/data/
fi
chown -R budgeter:budgeter /opt/budgeter/data
chown root:root /opt/budgeter /opt/budgeter/app
[ -f /opt/budgeter/.env ] || { echo "No /opt/budgeter/.env yet. Deploy once with -FirstRun." >&2; exit 1; }

install -m 644 "$here/budgeter.service" /etc/systemd/system/budgeter.service
install -m 755 "$here/budgeter" /usr/local/bin/budgeter
systemctl daemon-reload
systemctl enable --now budgeter
systemctl restart budgeter
sleep 3
systemctl --no-pager --lines=10 status budgeter || true
rm -rf "$here"
