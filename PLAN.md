# Plan: budgeter

Spec: [SPEC.md](SPEC.md) · Stack skill: `as-app-core`

## Slices

| Id | Goal (what the user can do after it) | ACs | Status | Base commit |
|---|---|---|---|---|
| S1 | Pilot: claim a SimpleFIN token into .env and list card balances | AC-1 | done | d9f872b |
| S2 | Sonnet categorizes history and proposes a budget; approve and show it | AC-2, AC-3 | done | b2de839 |
| S3 | `watch` polls every 2h and texts each new purchase (plain SMS via Twilio) | ~~AC-4~~, AC-7, AC-8, AC-9, AC-12 | done (print mode; real SMS waits on Twilio approval) | b8c2509 |
| S4 | Roast personality: harsh at bars, sassy elsewhere, plain for essentials; fallback if Sonnet fails | AC-5, AC-6, AC-10 | done | f8500d3 |
| S5 | Run it on the cloud machine as a service that survives restarts | AC-12 | building (deploy kit ready; needs a server) | e4f2f88 |
| S6 | Final: Capital One alert emails for near-real-time texts, no duplicates, get-home nudge | AC-13, AC-14, AC-15 | todo | |
| S7 | Alerts go to Telegram instead of SMS (`telegram link`); Twilio kept as an option | AC-16 | done (linked + test message; first real alert pending) | 86e15a9 |
| S8 | Chat with the bot: ask questions, change limits, `budget` and `help` | AC-17, AC-18, AC-19, AC-20 | done | f5393e4 |
| S9 | Find subscriptions, total them, flag price changes and new ones | AC-21, AC-22, AC-23, AC-24 | done | 6230cae |
| S10 | Odd-charge notes: double charges, way more than usual, first time at a merchant | AC-25, AC-26, AC-27 | review (tests pass; not tried on real data) | b4ed61e |
| S11 | Telegram buttons; propose and approve a budget from chat | AC-28, AC-32 | review (tests + sample data; not tried in Telegram) | 5fdda9b |
| S12 | Monthly wrap-up with tap-to-apply limit suggestions; `tune up` | AC-29 | review (tests + sample data; not tried in Telegram) | 5fdda9b |
| S13 | No-bar-tab streaks with praise at milestones; `streak` | AC-30 | review (tests + sample data; not tried in Telegram) | 5fdda9b |
| S14 | Charts: this month by category vs limit, and per day | AC-31 | review (tests + sample data; not tried in Telegram) | 5fdda9b |

Status is one of `todo`, `building`, `review`, `done`.

## S1: Pilot
- **Files:** src/Budgeter/{Program,SimpleFinClient,EnvFile}.cs, sample-accounts.json
- **Tests:** SimpleFinClientTests (token decode, claim, Basic auth, parsing), EnvFileTests
- **Run it:** `dotnet run --project src/Budgeter -- claim`, then `-- balances`
- **Done when:** tests pass and the user tried it.

## Gaps
<!-- ACs no slice covers yet. Never delete an AC to make this empty. -->
- none

## Decisions
- see `docs/decisions/`
