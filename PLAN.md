# Plan: budgeter

Spec: [SPEC.md](SPEC.md) · Stack skill: `as-app-core`

## Slices

| Id | Goal (what the user can do after it) | ACs | Status | Base commit |
|---|---|---|---|---|
| S1 | Pilot: claim a SimpleFIN token into .env and list card balances | AC-1 | done | d9f872b |
| S2 | Sonnet categorizes history and proposes a budget; approve and show it | AC-2, AC-3 | done | b2de839 |
| S3 | `watch` polls every 2h and texts each new purchase (plain SMS via Twilio) | AC-4, AC-7, AC-8, AC-9, AC-12 | done (print mode; real SMS waits on Twilio approval) | b8c2509 |
| S4 | Roast personality: harsh at bars, sassy elsewhere, plain for essentials; fallback if Sonnet fails | AC-5, AC-6, AC-10 | todo | |
| S5 | Run it on the cloud machine as a service that survives restarts | AC-12 | todo | |
| S6 | Final: Capital One alert emails for near-real-time texts, no duplicates, get-home nudge | AC-13, AC-14, AC-15 | todo | |

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
