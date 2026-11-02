# Showing the remaining monthly Claude budget

Notes from the research on 2026-11-02. Status: waiting for an Admin API key.

## Background

The company moved to a plan with a fixed monthly budget. In Claude Code, `/usage` now shows only:

```
Usage credits
1% used
$15.37 / $800.00 spent · Resets Dec 1 (Europe/Oslo)
```

That row is the Team/Enterprise "usage credits" view: your own spend this month against the limit the org set.

**Plan confirmed: Claude Enterprise** (company e-mail, 2026-11-02). Changes from 1 November:
- A 5 USD licence fee per user per month.
- Usage is paid at actual cost, per user, every month.
- Each user has a **monthly usage ceiling** tied to cost. It replaces the old weekly limits.
- Limits for Hedin IT are set with your Head Of. Ask them if you need a higher limit.
- The first months are a trial, and limits may be adjusted.

The Claude Code header still saying "Claude Team" is most likely a stale label from the migration.
Some default settings were still being worked through at the time of the e-mail.

A per-user monthly cost ceiling is exactly what the Enterprise Spend Limits API reports (Option 1).

## What Claude Code exposes locally

| Source | What it gives | Remaining budget? |
|---|---|---|
| `/usage` | Usage-credits row (spend / limit / reset date) | Interactive only. No CLI flag, JSON output or local file |
| Hook inputs | `session_id`, `prompt_id`, `transcript_path`, `cwd`, `hook_event_name`, ... | No cost or budget fields |
| Status line JSON: `cost.total_cost_usd` | Session cost estimate at list price, resets on `/clear` | No, spend only |
| Status line JSON: `rate_limits.five_hour` / `seven_day` | `used_percentage`, `resets_at` | Documented for Pro/Max/Ultra only, not Team/Enterprise |
| Status line JSON: `rate_limits.spend_limit` | `used_usd`, `limit_usd`, `period` | Only behind a self-hosted Claude apps gateway |
| OpenTelemetry `claude_code.cost.usage` | Cost in USD | No. Docs say no metric reports remaining budget |

Docs: https://code.claude.com/docs/en/statusline.md, https://code.claude.com/docs/en/costs.md,
https://code.claude.com/docs/en/monitoring-usage.md

## Option 1: Spend Limits API (Enterprise, needs an Admin API key)

```
GET https://api.anthropic.com/v1/organizations/spend_limits/effective?period[]=monthly&user_ids[]=<id>
x-api-key: <admin key with read:spend_limits scope>
anthropic-version: 2023-06-01
```

- Response `data[]`:
  - `amount`: the cap, as a string in minor units (cents). `null` means unlimited.
  - `period_to_date_spend`: spend so far, as a string in cents. It can have up to 3 decimals.
  - Also `currency`, `period` (`daily|weekly|monthly`), `scope`, `source`, `actor`, `spend_limit_id`, and `next_page` for pagination.
- **Remaining = (`amount` - `period_to_date_spend`) / 100** dollars.
- Query parameters: `limit` (default 20, max 1000), `page`, `period[]`, `user_ids[]` (up to 100).
- Documented as Enterprise only. It isn't available to Console orgs, and no Team equivalent is documented.
- Docs:
  - https://platform.claude.com/docs/en/manage-claude/spend-limits-api
  - https://platform.claude.com/docs/en/api/beta/organization/spend_limits/effective

Unverified:
- The auth header: the docs text says `x-api-key`, but the example uses `Authorization: Bearer`. Test both.
- Whether `period_to_date_spend` matches the number in `/usage`. The docs say it covers usage-credit spend only.
- How to find your own `user_id`. Leaving out `user_ids[]` may return org-level limits instead.

**Ask the Claude Enterprise admin for:**
1. A read-only Admin API key with only the `read:spend_limits` scope.
2. Optionally, your own `user_id` in the org, so the query can be filtered to you.

Your Head Of sets the limit, but probably isn't the one who issues API keys. Ask whoever administers the Claude org.

Store the key outside the repo, for example in Windows Credential Manager or an environment variable. Never put it in `settings.json`.

## Option 2: Local estimate (works without an admin)

1. Use a status line command to forward `cost.total_cost_usd` per session to the tray app, for example `POST localhost:8787/cost`.
2. The app adds up the deltas for each session within the calendar month. Start from a baseline you enter by hand, such as $15.37 on 2026-11-02, and use a configured cap of $800.
3. Show "~$X left of $800" in the tooltip and menu. Optionally change the icon when spend gets close to the cap.

Limitations:
- It's an estimate at list price.
- It only counts this PC, not claude.ai web or other machines.
- It drifts over time. Re-entering the real figure from `/usage` corrects it.

## Other billing setups, for reference

Console (API) billing:
- Spend-to-date is available from `GET /v1/organizations/cost_report`, or from `GET /v1/organizations/analytics/cost` (beta).
- No endpoint returns the monthly cap or the remaining credit.
- See https://platform.claude.com/docs/en/manage-claude/usage-cost-api

Signs that the budget has run out:
- HTTP 429 with `error_code: enforced_spend_limit_reached`
- HTTP 400 "You have reached your specified API usage limits"

## Next step

Once an admin key is available:
1. Test the call with curl.
2. Confirm the numbers match `/usage`.
3. Add polling to the tray app, every 5–10 minutes with a timeout. Show the remaining amount in the tooltip and fail silently when offline, the same way the ESP push does.
