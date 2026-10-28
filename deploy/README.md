# Deploying Budgeter

Runs `budgeter watch` on an Ubuntu server as a systemd service (see `docs/decisions/0002-hosting.md`).

## One-time server setup
1. Create an Ubuntu 24.04 VPS (1 GB RAM) with your SSH key, on any provider.
2. Check you can log in: `ssh root@<server-ip>`.

## Deploy
From the repo root on Windows:

```powershell
pwsh deploy/Deploy-Budgeter.ps1 -Server root@<server-ip> -FirstRun          # dry run: shows what it will copy
pwsh deploy/Deploy-Budgeter.ps1 -Server root@<server-ip> -FirstRun -Apply   # first deploy: app + .env + data/
pwsh deploy/Deploy-Budgeter.ps1 -Server root@<server-ip> -Apply             # later deploys: app only
```

`-FirstRun` copies your `.env` (keys) and `data/` (budget, categories, watch state). Later deploys leave the
server's copies alone. Stop any `watch` running on your PC first, or both will text you.

## On the server
- Logs: `journalctl -u budgeter -f`
- Status: `systemctl status budgeter`
- Any command: `budgeter budget show`, `budgeter budget propose`, `budgeter balances`
- Restart after editing `/opt/budgeter/.env`: `systemctl restart budgeter`
