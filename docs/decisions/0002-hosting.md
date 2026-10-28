# 0002: Host on a small Ubuntu VPS as a systemd service

Date: 2026-10-28

## Decision
Run `budgeter watch` on any Ubuntu 24.04 VPS (1 GB RAM is plenty) as the `budgeter` systemd service, deployed
from Windows with `deploy/Deploy-Budgeter.ps1`. The app ships self-contained for linux-x64, so the server
needs no .NET install, only ICU (install.sh adds it if missing).

## Why
- Cheapest always-on option (a few dollars a month) and provider-neutral: Hetzner, DigitalOcean, Linode and
  others all work the same way.
- systemd gives restart-on-crash and start-on-boot for free (AC-12).
- Personal app, so a personal server: not the work Azure tenant.
- Later slices (text replies, a web page) need a public HTTPS address; a VPS can add that with a reverse proxy
  (e.g. Caddy) without changing hosts.

## Consequences
- `TZ` is set in the unit file from the deploying PC's time zone, so months roll over at local midnight.
- `.env` and `data/` are copied only on the first deploy (`-FirstRun`); later deploys replace the app only.
- Only one `watch` may run at a time: stop any local `watch` once the server is live, or both will text.
