# budgeter: AI instructions

Personal app built with the agenticSkills system. Every assistant follows these rules.

## Stack
- Stack: .NET 8 console app (SimpleFIN for card data, Sonnet, Twilio SMS), xUnit tests
- Core rules: `as-app-core`
- Run it: `dotnet run --project src/Budgeter -- balances --sample` (drop `--sample` for real data; `claim` first)
- Watch: `dotnet run --project src/Budgeter -- watch --print --once` (print mode; drop `--print` to text, `--once` to keep polling)
- Test it: `dotnet test`

## Where state lives
`SPEC.md` (what and acceptance criteria), `PLAN.md` (slices and status), `VERIFY.md` (verdicts),
`docs/decisions/` (why). Resume from these files; don't re-ask what they answer.

## Rules
- Pushes, tags, releases, deploys, paid API calls and writes outside this folder wait for a yes.
- Secrets go in `.env` (git-ignored); `.env.example` lists the names only.
- A slice is done when its tests pass and the user tried it.
