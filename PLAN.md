# Plan: budgeter

Spec: [SPEC.md](SPEC.md) · Stack skill: `as-app-core`

## Slices

| Id | Goal (what the user can do after it) | ACs | Status | Base commit |
|---|---|---|---|---|
| S1 | Pilot: claim a SimpleFIN token into .env and list card balances | (spec pending) | done | |
| S2 | Sonnet builds a budget from recent transactions | (spec pending) | todo | |
| S3 | New purchase -> Sonnet roast -> SMS via Twilio | (spec pending) | todo | |

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
