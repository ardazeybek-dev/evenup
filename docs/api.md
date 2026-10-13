# HTTP API

`evenup serve` starts a small HTTP server. It serves the playground at `/` and the JSON API below. Every request body is a ledger in [`.evenup` format](format.md), sent as `text/plain`.

| Method | Path | Returns |
|---|---|---|
| `GET` | `/api/health` | `{ "status": "ok", "version": "0.1.0", "exactLimit": 20 }` |
| `GET` | `/api/examples` | Names of the bundled example ledgers |
| `GET` | `/api/examples/{name}` | The source of one example, as `text/plain` |
| `POST` | `/api/settle` | Settlement as JSON (shape below) |
| `POST` | `/api/report` | The self-contained HTML report |

Query parameters for `POST /api/settle` and `POST /api/report`:

- `exactLimit=N` overrides the exact-solver limit for one request. It is capped at 22.
- `greedy` skips the exact solver. It works on `/api/settle` only.

## Example

```bash
curl -s -X POST --data-binary @examples/greedy-trap.evenup \
     -H 'content-type: text/plain' localhost:5317/api/settle
```

```jsonc
{
  "group": "Greedy trap",
  "currency": "EUR",
  "period": "",
  "totalSpend": { "minor": 90000, "text": "€900.00" },
  "people": [
    { "name": "ana", "paid": {…}, "share": {…}, "sent": {…}, "received": {…},
      "net": { "minor": 50000, "text": "€500.00" } }
  ],
  "entries":   [ /* every line after splitting and conversion */ ],
  "before":    [ /* direct debts: { from, to, amount } */ ],
  "transfers": [ { "from": "ben", "to": "ana", "amount": { "minor": 30000, "text": "€300.00" } } ],
  "plan": {
    "method": "optimal",          // optimal | greedy | hybrid
    "participants": 5,
    "groups": [["ana","ben","cleo"], ["dan","eli"]],
    "naiveTransfers": 3,
    "greedyTransfers": 4,
    "transfers": 3
  },
  "svg": { "before": "<svg…>", "after": "<svg…>" }
}
```

Every amount comes in two forms: `minor`, an integer in the currency's smallest unit, and `text`, formatted for display. Use `minor` for any further arithmetic.

## Errors

When the ledger is invalid, the API responds with `422 Unprocessable Entity` and lists every problem it found:

```json
{ "errors": [ { "line": 2, "column": 12, "message": "'12.345' has more than 2 decimal place(s)" } ] }
```

A body larger than `EVENUP_MAX_BODY_KB` is rejected with `413`.
