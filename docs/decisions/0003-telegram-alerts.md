# 0003: Send alerts through Telegram, not SMS

Date: 2026-10-29

## Decision
Purchase alerts go to a Telegram bot chat linked with `budgeter telegram link`. Twilio SMS stays in the code as
an optional sender (used only when no `TELEGRAM_BOT_TOKEN` is set).

## Why
- US carriers require an approved A2P 10DLC campaign for app-to-person SMS. The campaign was rejected twice
  (error 30909, opt-in not verifiable) even after publishing a public keyword opt-in page: the process is built
  for businesses texting customers, not one person texting themselves.
- Telegram bots need no approval, cost nothing per message, and carry no carrier content review, so the bar
  roasts can stay harsh.
- The bot can read replies by polling Telegram, which makes "reply to change the budget" possible without a
  public web address, login, or HTTPS setup on the server.

## Consequences
- Alerts arrive as Telegram notifications, not texts; Telegram must be installed on the phone.
- The bot token is a secret (it's in every Bot API URL), so it lives in `.env` and URLs are never printed.
- Whoever messages the bot first could be linked, so `telegram link` shows the chat's name and asks before saving.
