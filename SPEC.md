# Spec: budgeter

Status: approved 2026-10-27 · changed 2026-10-29 (alerts moved from SMS to Telegram: AC-4 -> AC-16; chat with the bot: AC-17 to AC-20; subscriptions: AC-21 to AC-24; odd charges, buttons, wrap-ups, streaks, charts: AC-25 to AC-32)

## Problem
My credit card spending creeps up and I only notice when the statement lands. I want a budget built from what I
actually spend, and a message after each purchase that tells me where I stand, with enough attitude that I listen,
especially when I'm at a bar.

## Users and setting
Just me. Runs unattended on a cloud Linux machine; I see alerts as Telegram messages on my phone and run the odd CLI command over SSH.

## Acceptance criteria
- [x] AC-1: Given a SimpleFIN setup token, when I run `claim` and then `balances`, then each linked card shows its name, balance and as-of time, and the access URL is saved to `.env` without being printed.
- [x] AC-2: Given at least one month of transaction history, when I run `budget propose`, then Sonnet prints monthly limits per category with the spending they were based on, and nothing takes effect yet.
- [x] AC-3: Given a proposed budget, when I run `budget approve` (optionally after editing `budget.json`), then that budget becomes the active one and `budget show` lists each category's limit, spent so far this month and what's left.
- ~~AC-4~~ dropped: replaced by AC-16. SMS needed an approved A2P 10DLC campaign, and carriers twice rejected a campaign that only texts its owner (see `docs/decisions/0003-telegram-alerts.md`).
- [x] AC-5: Given a purchase categorized as bars, when its SMS is written, then the comment is harsh and profanity is allowed, and it gets harsher the further bars is over its monthly pace.
- [x] AC-6: Given a purchase in another discretionary category (takeout, shopping, entertainment, etc.), when its SMS is written, then the comment is sassy but not vicious; essentials (groceries, gas, bills, health) get a plain, no-roast message.
- [x] AC-7: Given `watch` starts for the first time, when it reads existing history, then it marks those transactions as seen and sends no texts for them.
- [x] AC-8: Given a transaction already texted, when it shows up again in a later poll (or after a restart), then no second SMS is sent.
- [x] AC-9: Given a payment to the card or a refund, when `watch` sees it, then no SMS is sent; a refund lowers that category's spent amount.
- [x] AC-10: Given Sonnet fails or times out, when a purchase needs a text, then a plain SMS without the comment is sent instead; given Twilio fails, then the text is retried on the next poll and not lost.
- ~~AC-11~~ dropped: no quiet hours; a bar roast is only useful while I'm still at the bar.
- [ ] AC-12: Given the app runs on the cloud machine, when it polls, then it calls SimpleFIN at most once every 2 hours (within the Bridge's daily limit) and keeps running across restarts.
- [ ] AC-13: Given Capital One emails me a purchase alert, when the email reaches my inbox, then I get the alert within 5 minutes, at any hour.
- [ ] AC-14: Given a purchase was already texted from an alert email, when SimpleFIN later returns the same purchase (same card, amount, merchant and date), then no second alert is sent.
- [ ] AC-15: Given it's my second or later bar purchase of the night, or any bar purchase after 10 PM, when its SMS is written, then it also tells me to stop and get a ride home, not drive.
- [ ] AC-16: Given a Telegram bot linked to my chat with `telegram link`, when `watch` finds a posted purchase it hasn't seen before, then I get one Telegram message with the amount, merchant, its category, what's left in that category this month, and a one-line comment; messages go only to the linked chat.
- [x] AC-17: Given my linked Telegram chat, when I send a question such as "what did I spend today?" or "how much on bars this week?", then the bot replies within a minute with an answer computed from my saved transactions and budget, and says how fresh that data is.
- [x] AC-18: Given I ask to change a limit ("set bars to 200", "bars 200"), when the bot understands it, then the active budget changes and the reply confirms the new limit and what's left this month.
- [x] AC-19: Given a message from any chat other than mine, when the bot receives it, then it sends no reply and reveals nothing about my data.
- [x] AC-20: Given I send `budget` or `help`, when the bot answers, then it uses no Sonnet call; and no message ever makes Budgeter fetch from SimpleFIN.
- [x] AC-21: Given at least two charges from a merchant at a steady weekly, monthly or yearly rhythm and price, when I send `subscriptions` (in chat or the CLI), then I get each subscription with its price, how often it charges and when it's next expected, plus the monthly and yearly total; recurring bills whose amount varies (like utilities) are listed separately, outside that total.
- [x] AC-22: Given a known subscription, when a new charge for it has a different price than the last one, then that purchase's alert says the old and new price and what the change costs per year.
- [x] AC-23: Given at least 5 weeks of history, when a purchase categorized as subscriptions comes from a merchant never charged before, then its alert flags it as a possible new subscription.
- [x] AC-24: Given a subscription that hasn't charged within 10 days after it was expected (4 for weekly), when I ask for subscriptions, then it's marked as possibly canceled and left out of the total.
- [x] AC-25: Given a charge with the same amount at the same merchant as one posted within the previous 24 hours, when it's alerted, then the alert asks whether it's a double charge (only the second one is flagged).
- [x] AC-26: Given at least 3 earlier charges at a merchant, when a new one is at least 3 times the usual (median) and at least $25 more, then its alert says it's way more than usual and what usual is.
- [x] AC-27: Given at least 5 weeks of history, when a purchase comes from a merchant never charged before (and it isn't a subscription), then its alert says it's the first time there.
- [ ] AC-28: Given my linked chat, when I send `propose`, then the bot replies with a proposed budget (limit and average per category) and Approve / Discard buttons; tapping Approve makes it the active budget and confirms it, Discard throws it away.
- [ ] AC-29: Given an active budget and last month in the history, when the first sync of a new month runs, then I get last month's wrap-up (spent vs limit, where I went over) and up to 3 suggested limit changes, each with an Apply button that changes that limit when tapped; it comes once per month, and `tune up` asks for one any time.
- [ ] AC-30: Given no bar charge has posted for 3, 7, 14, 30, 60 or 90 days, when a sync passes that mark, then I get one short message of praise for it; a new bar charge restarts the count, and `streak` shows the current one.
- [ ] AC-31: Given synced data, when I send `chart` or ask to see this month, then the bot sends an image of this month's spending against each limit; `daily chart` sends spending per day. Charts are drawn on the machine; the data isn't sent to any chart service.
- [ ] AC-32: Given a button tap from any chat other than mine, when the bot receives it, then nothing changes and nothing is revealed.

## Non-goals (not in this version)
- Alerts from cards that don't send purchase emails (those wait for SimpleFIN's daily refresh)
- Bank/checking accounts, income, net worth, or anything that moves money
- A web or phone UI; multiple users
- Investment or financial advice

## Constraints
- History: SimpleFIN returns about 90 days, so Budgeter keeps its own growing history; yearly subscriptions only show up once it has seen two charges a year apart.
- Data: transactions and the budget stay on the cloud machine (local files or SQLite); SimpleFIN access URL, Anthropic key and Telegram bot token in `.env` only, never committed or printed.
- Cost: SimpleFIN ~$15/yr; Telegram is free; Sonnet calls are pennies a month (one categorize-and-comment call per purchase, one per budget proposal).
- Platform: headless .NET 8 console app on a Linux cloud VM; alerts only go to my own Telegram chat.
- SMS via Twilio stays in the code as an optional sender, for if a 10DLC campaign is ever approved.

## Stack
`other` (.NET 8 console + xUnit): same stack as marathoncoach, which I already know; the Anthropic .NET SDK plus plain HTTP calls to Telegram's Bot API cover what's needed.
