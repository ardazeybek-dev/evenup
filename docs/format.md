# The `.evenup` ledger format

A ledger is a plain-text file. You can read it, diff it, keep it in a git repo or paste it into a chat. Each line is either a **directive**, an **expense**, a **payment** or a **comment**.

```text
group "Lisbon Trip 2026"      # name shown in reports
currency EUR                  # base currency: every balance ends up in it
rate USD 0.92                 # 1 USD = 0.92 EUR
people alice bob carol        # declared members (typos become errors)

2026-05-01 "Airbnb" alice paid 840
2026-05-02 "Dinner" bob paid 126.40 split shares alice:2 bob:1 carol:1
2026-05-03 carol paid alice 20
```

## Directives

| Directive | Meaning |
|---|---|
| `group <name…>` | Title of the ledger. Quotes are optional. |
| `currency <CODE>` | Base currency (ISO 4217). Defaults to `USD`. It must come before the first expense. |
| `rate <CODE> <value>` | Exchange rate: 1 unit of `CODE` = `value` units of the base currency. |
| `people <name…>` | Members of the group. Optional, but once it is there, every name used later must be on it. Then a typo like `bbo` is reported as *did you mean 'bob'?* instead of quietly becoming a new person. |

## Expenses

```text
[date] ["title"] <payer> paid <amount> [CODE] [split rule]
```

- **date**: optional, `yyyy-MM-dd`.
- **title**: optional, in double quotes.
- **amount**: uses a dot for decimals, and `_` can group thousands (`1_140.50`). It can't have more decimals than the currency has: `12.345 EUR` is an error and `100.5 JPY` is an error. A negative amount is a refund.
- **CODE**: an optional three-letter currency. When given, the ledger needs a `rate` for it.

### Payers

| Form | Example |
|---|---|
| One person | `alice paid 90` |
| Several people with fixed parts | `alice:40, emma:56.30 paid 96.30` (the parts must add up to the total) |

### Split rules

| Rule | Example | Meaning |
|---|---|---|
| *(none)* | `alice paid 90` | Equal split among everyone in the group. |
| `for` | `alice paid 90 for bob carol` | Equal split among the listed people only. The payer is not included unless listed. |
| `split equally [among …]` | `split equally among alice bob` | Same as `for`, in long form. |
| `split shares` | `split shares alice:2 bob:1` | Proportional to weights such as nights stayed or room size. Decimals are fine (`1.5`). |
| `split percent` | `split percent alice:60 bob:40%` | The percentages must add up to exactly 100. |
| `split exact` | `split exact alice:20 bob:13.17` | Fixed amounts that must add up to the total. |
| `itemized` | see below | Receipt lines, with tax and tip spread fairly. |

### Itemized receipts

Write `itemized` (or `split itemized`) at the end of the line. Then list one indented line per receipt item: an optional quoted label, one or more names, and the item price. An item with several names is shared equally between them.

```text
2026-05-02 "Time Out Market" farid paid 214.70 itemized
    "Bacalhau"            alice 24.50
    "Francesinha"         carol, dave 31.00
    "Two bottles of wine" alice bob carol dave emma farid 58.00
```

The items can add up to less than the total, usually because of tax, a tip or a service charge. In that case the difference is spread **in proportion to each person's subtotal**: whoever ordered the lobster also pays more of the tip. When the items add up to more than the total (a discount), the discount is spread the same way.

## Payments

When money has already changed hands, record it as a payment:

```text
2026-05-05 carol paid alice 100
2026-05-06 farid paid dave 40 GBP
```

A payment looks like an expense, except that a person's name follows `paid` instead of an amount.

## Names

Names start with a letter from any script, so `ayşe`, `zoë` and `İlker` all work. After that they may contain letters, digits, `_` and `-`. Names are case-sensitive. A name can't be a keyword (`paid`, `for`, `split`…). A currency code is only recognised directly after an amount.

## Comments and whitespace

`#` starts a comment that runs to the end of the line. It does not start a comment inside a quoted title. Blank lines are ignored. Only item lines may be indented. Commas work like spaces, so `carol, dave` and `carol dave` mean the same thing.

## Errors

Every problem is reported with its line and column, and all of them are reported in one run:

```text
error: trip.evenup:12:12 '12.345' has more than 2 decimal place(s)
  12 | alice paid 12.345
     |            ^
```

The exit codes are `0` (ok), `1` (the ledger has errors) and `2` (bad command-line usage).
