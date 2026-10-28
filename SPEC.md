# Spec: budgeter

Status: approved 2026-10-27

## Problem
My credit card spending creeps up and I only notice when the statement lands. I want a budget built from what I
actually spend, and a text after each purchase that tells me where I stand, with enough attitude that I listen,
especially when I'm at a bar.

## Users and setting
Just me. Runs unattended on a cloud Linux machine; I see it as SMS on my phone and run the odd CLI command over SSH.

## Acceptance criteria
- [x] AC-1: Given a SimpleFIN setup token, when I run `claim` and then `balances`, then each linked card shows its name, balance and as-of time, and the access URL is saved to `.env` without being printed.
- [ ] AC-2: Given at least one month of transaction history, when I run `budget propose`, then Sonnet prints monthly limits per category with the spending they were based on, and nothing takes effect yet.
- [ ] AC-3: Given a proposed budget, when I run `budget approve` (optionally after editing `budget.json`), then that budget becomes the active one and `budget show` lists each category's limit, spent so far this month and what's left.
- [ ] AC-4: Given the app is running in `watch` mode, when SimpleFIN returns a posted purchase it hasn't seen before, then I get one SMS with the amount, merchant, its category, what's left in that category this month, and a one-line comment.
- [ ] AC-5: Given a purchase categorized as bars, when its SMS is written, then the comment is harsh and profanity is allowed, and it gets harsher the further bars is over its monthly pace.
- [ ] AC-6: Given a purchase in another discretionary category (takeout, shopping, entertainment, etc.), when its SMS is written, then the comment is sassy but not vicious; essentials (groceries, gas, bills, health) get a plain, no-roast message.
- [ ] AC-7: Given `watch` starts for the first time, when it reads existing history, then it marks those transactions as seen and sends no texts for them.
- [ ] AC-8: Given a transaction already texted, when it shows up again in a later poll (or after a restart), then no second SMS is sent.
- [ ] AC-9: Given a payment to the card or a refund, when `watch` sees it, then no SMS is sent; a refund lowers that category's spent amount.
- [ ] AC-10: Given Sonnet fails or times out, when a purchase needs a text, then a plain SMS without the comment is sent instead; given Twilio fails, then the text is retried on the next poll and not lost.
- ~~AC-11~~ dropped: no quiet hours; a bar roast is only useful while I'm still at the bar.
- [ ] AC-12: Given the app runs on the cloud machine, when it polls, then it calls SimpleFIN at most once every 2 hours (within the Bridge's daily limit) and keeps running across restarts.
- [ ] AC-13: Given Capital One emails me a purchase alert, when the email reaches my inbox, then I get the SMS within 5 minutes, at any hour.
- [ ] AC-14: Given a purchase was already texted from an alert email, when SimpleFIN later returns the same purchase (same card, amount, merchant and date), then no second SMS is sent.
- [ ] AC-15: Given it's my second or later bar purchase of the night, or any bar purchase after 10 PM, when its SMS is written, then it also tells me to stop and get a ride home, not drive.

## Non-goals (not in this version)
- Alerts from cards that don't send purchase emails (those wait for SimpleFIN's daily refresh)
- Bank/checking accounts, income, net worth, or anything that moves money
- Replying to the SMS to change the budget (edit `budget.json` and use the CLI instead)
- A web or phone UI; multiple users
- Investment or financial advice

## Constraints
- Data: transactions and the budget stay on the cloud machine (local files or SQLite); SimpleFIN access URL, Anthropic and Twilio keys in `.env` only, never committed or printed.
- Cost: SimpleFIN ~$15/yr; Twilio number + A2P 10DLC sole-proprietor registration + a few cents per text; Sonnet calls are pennies a month (one categorize-and-comment call per purchase, one per budget proposal).
- Platform: headless .NET 8 console app on a Linux cloud VM; SMS only goes to my own number.
- SMS: US carriers require A2P 10DLC registration before texts are reliably delivered; until then, test against a Twilio trial with my verified number.

## Stack
`other` (.NET 8 console + xUnit): same stack as marathoncoach, which I already know; the Anthropic .NET SDK and Twilio's .NET SDK cover what's needed.
