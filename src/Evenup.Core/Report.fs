namespace Evenup

open System
open System.Globalization
open System.Text

/// Everything the outside world sees: JSON, Markdown and the self-contained HTML report.
module Report =

    let private inv = CultureInfo.InvariantCulture
    let private esc = Svg.escape

    let methodName =
        function
        | Optimal -> "optimal"
        | Greedy -> "greedy"
        | Hybrid -> "hybrid"

    let private dateText (d: DateOnly option) =
        match d with
        | Some d -> d.ToString("yyyy-MM-dd", inv)
        | None -> ""

    let dateRange (a: Analysis) =
        let dates = a.Entries |> List.choose (fun e -> e.Date)
        if List.isEmpty dates then ""
        else
            let lo, hi = List.min dates, List.max dates
            let fmt (d: DateOnly) = d.ToString("MMM d, yyyy", inv)
            if lo = hi then fmt lo else sprintf "%s – %s" (fmt lo) (fmt hi)

    /// Plain data for JSON (System.Text.Json serialises anonymous records cleanly).
    let toDto (a: Analysis) (plan: Plan) (withSvg: bool) =
        let cur = a.Ledger.Base
        let money (v: int64) = {| minor = v; text = Money.format cur v |}
        let edge (t: Transfer) = {| from = t.From; ``to`` = t.To; amount = money t.Amount |}
        let balances = a.Balances |> List.map (fun b -> b.Person, b.Net)
        {| group = a.Ledger.Name
           currency = cur
           period = dateRange a
           totalSpend = money a.TotalSpend
           people =
            a.Balances
            |> List.map (fun b ->
                {| name = b.Person
                   paid = money b.Paid
                   share = money b.Share
                   sent = money b.Sent
                   received = money b.Received
                   net = money b.Net |})
           entries =
            a.Entries
            |> List.map (fun e ->
                {| line = e.Line
                   date = dateText e.Date
                   title = e.Title
                   kind = e.Kind
                   split = e.SplitKind
                   amount = {| minor = e.Amount; text = Money.format e.Currency e.Amount; currency = e.Currency |}
                   baseAmount = money e.BaseAmount
                   paidBy = e.Paid |> List.map (fun (p, v) -> {| name = p; amount = money v |})
                   shares = e.Owed |> List.map (fun (p, v) -> {| name = p; amount = money v |}) |})
           before = a.PairwiseDebts |> List.map edge
           transfers = plan.Transfers |> List.map edge
           plan =
            {| ``method`` = methodName plan.Method
               participants = plan.Participants
               groups = plan.Groups
               naiveTransfers = a.PairwiseDebts.Length
               greedyTransfers = (Settle.greedy balances).Length
               transfers = plan.Transfers.Length |}
           svg =
            if withSvg then
                {| before = Svg.graph "before" balances a.PairwiseDebts (Money.format cur)
                   after = Svg.graph "after" balances plan.Transfers (Money.format cur) |}
            else
                {| before = ""; after = "" |} |}

    let markdown (a: Analysis) (plan: Plan) =
        let cur = a.Ledger.Base
        let m = Money.format cur
        let sb = StringBuilder()
        let line (s: string) = sb.AppendLine s |> ignore
        line (sprintf "# %s" a.Ledger.Name)
        line ""
        line (sprintf "Total spent **%s** across %d entries. %d direct debts settle in **%d transfer(s)** (%s)."
                  (m a.TotalSpend) a.Entries.Length a.PairwiseDebts.Length plan.Transfers.Length (methodName plan.Method))
        line ""
        line "## Settle up"
        line ""
        if List.isEmpty plan.Transfers then line "Everyone is square."
        for t in plan.Transfers do
            line (sprintf "- **%s** pays **%s** %s" t.From t.To (m t.Amount))
        line ""
        line "## Balances"
        line ""
        line "| Person | Paid | Share | Payments | Net |"
        line "|---|--:|--:|--:|--:|"
        for b in a.Balances do
            line (sprintf "| %s | %s | %s | %s | %s |" b.Person (m b.Paid) (m b.Share) (m (b.Sent - b.Received)) (m b.Net))
        sb.ToString()

    /// Design tokens shared by the report and the playground; light and dark.
    let themeCss =
        """
:root{
  --bg:#f5f2ea;--surface:#fffdf7;--surface-2:#f9f6ee;--ink:#1c1a15;--muted:#6d675b;--line:#e3ddcf;
  --pos:#0d7a5c;--pos-soft:#dcf2e8;--neg:#c23f2a;--neg-soft:#fbe4de;--edge:#57534a;--accent:#3346c9;
  --shadow:0 1px 0 rgba(28,26,21,.04),0 8px 24px -12px rgba(28,26,21,.18);
  --font-ui:"Inter",system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
  --font-mono:"JetBrains Mono",ui-monospace,"SFMono-Regular",Menlo,Consolas,monospace;
  --font-display:"Iowan Old Style","Palatino Linotype",Palatino,Georgia,serif;
  color-scheme:light;
}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){
  --bg:#111210;--surface:#1a1b19;--surface-2:#20221f;--ink:#edeae2;--muted:#a39e92;--line:#2f312d;
  --pos:#4fcf9f;--pos-soft:#12392b;--neg:#ff8068;--neg-soft:#41201a;--edge:#a8a397;--accent:#8f9dff;
  --shadow:0 1px 0 rgba(0,0,0,.3),0 10px 30px -14px rgba(0,0,0,.7);color-scheme:dark}}
:root[data-theme="dark"]{
  --bg:#111210;--surface:#1a1b19;--surface-2:#20221f;--ink:#edeae2;--muted:#a39e92;--line:#2f312d;
  --pos:#4fcf9f;--pos-soft:#12392b;--neg:#ff8068;--neg-soft:#41201a;--edge:#a8a397;--accent:#8f9dff;
  --shadow:0 1px 0 rgba(0,0,0,.3),0 10px 30px -14px rgba(0,0,0,.7);color-scheme:dark}
"""
        + Svg.css

    let private reportCss =
        """
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 var(--font-ui);-webkit-font-smoothing:antialiased}
.wrap{max-width:1120px;margin:0 auto;padding:48px 24px 72px}
.eyebrow{font:600 12px/1 var(--font-mono);letter-spacing:.14em;text-transform:uppercase;color:var(--muted)}
h1{font:600 clamp(32px,5vw,52px)/1.05 var(--font-display);letter-spacing:-.01em;margin:14px 0 10px}
h2{font:600 22px/1.2 var(--font-display);margin:0 0 4px}
.sub{color:var(--muted);margin:0}
.meta{color:var(--muted);display:flex;flex-wrap:wrap;gap:6px 18px;margin-top:6px}
.kpis{display:grid;grid-template-columns:repeat(4,minmax(0,1fr));gap:14px;margin:34px 0 40px}
.kpi{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:18px 20px;box-shadow:var(--shadow)}
.kpi .k{font-size:12.5px;color:var(--muted)}
.kpi .v{font:600 26px/1.2 var(--font-mono);font-variant-numeric:tabular-nums;margin-top:6px;letter-spacing:-.02em}
.kpi .v small{font-size:14px;color:var(--muted);font-weight:500}
.kpi.hl{background:var(--ink);color:var(--bg);border-color:var(--ink)}
.kpi.hl .k,.kpi.hl .v small{color:color-mix(in srgb,var(--bg) 70%,transparent)}
section{margin:0 0 44px}
.head{display:flex;justify-content:space-between;align-items:end;gap:16px;margin-bottom:16px}
.receipt{background:var(--surface);border:1px solid var(--line);border-radius:18px;box-shadow:var(--shadow);padding:6px 0;position:relative}
.receipt:after{content:"";position:absolute;left:0;right:0;bottom:-9px;height:10px;
  background:radial-gradient(circle at 7px -2px,transparent 7px,var(--surface) 7.5px) repeat-x;background-size:14px 10px}
.tx{display:grid;grid-template-columns:auto 1fr auto;align-items:center;gap:14px;padding:14px 22px;border-bottom:1px dashed var(--line)}
.tx:last-child{border-bottom:0}
.who{display:flex;align-items:center;gap:10px;font-weight:600}
.av{width:30px;height:30px;border-radius:50%;display:grid;place-items:center;font:700 13px var(--font-ui);border:2px solid}
.av.neg{background:var(--neg-soft);border-color:var(--neg)}
.av.pos{background:var(--pos-soft);border-color:var(--pos)}
.arrow{color:var(--muted);font-size:13px;display:flex;align-items:center;gap:10px}
.arrow:before{content:"";flex:1;height:1px;background:linear-gradient(90deg,var(--line),var(--muted))}
.amt{font:600 18px var(--font-mono);font-variant-numeric:tabular-nums}
.square{padding:28px;text-align:center;color:var(--muted)}
.graphs{display:grid;grid-template-columns:1fr 1fr;gap:18px}
.card{background:var(--surface);border:1px solid var(--line);border-radius:18px;padding:20px 20px 10px;box-shadow:var(--shadow)}
.card h3{margin:0;font-size:15px}
.card p{margin:2px 0 6px;color:var(--muted);font-size:13px}
.badge{display:inline-block;font:600 11px/1 var(--font-mono);padding:5px 8px;border-radius:99px;background:var(--surface-2);border:1px solid var(--line);color:var(--muted);vertical-align:2px;margin-left:6px}
.badge.ok{background:var(--pos-soft);border-color:transparent;color:var(--pos)}
table{width:100%;border-collapse:collapse;background:var(--surface);border:1px solid var(--line);border-radius:16px;overflow:hidden;box-shadow:var(--shadow)}
th,td{padding:11px 16px;text-align:left;border-bottom:1px solid var(--line);vertical-align:middle}
th{font:600 11.5px var(--font-mono);letter-spacing:.08em;text-transform:uppercase;color:var(--muted);background:var(--surface-2)}
tr:last-child td{border-bottom:0}
td.n,th.n{text-align:right;font-family:var(--font-mono);font-variant-numeric:tabular-nums;white-space:nowrap}
td.pos{color:var(--pos);font-weight:600}td.neg{color:var(--neg);font-weight:600}
.bar{position:relative;height:10px;min-width:120px;background:var(--surface-2);border-radius:99px}
.bar i{position:absolute;top:0;bottom:0;border-radius:99px}
.bar .mid{position:absolute;left:50%;top:-3px;bottom:-3px;width:1px;background:var(--muted);opacity:.5}
.muted{color:var(--muted)}
.chip{font:500 11.5px var(--font-mono);color:var(--muted);background:var(--surface-2);border:1px solid var(--line);border-radius:6px;padding:2px 6px;white-space:nowrap}
.scroll{overflow-x:auto;border-radius:16px}
footer{margin-top:56px;color:var(--muted);font-size:13px;display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap}
footer a{color:inherit}
@media (max-width:860px){.kpis{grid-template-columns:repeat(2,minmax(0,1fr))}.graphs{grid-template-columns:1fr}}
@media (max-width:520px){.wrap{padding:28px 16px 56px}.tx{grid-template-columns:1fr auto;padding:12px 16px}.arrow{display:none}.kpi .v{font-size:21px}}
@media print{body{background:#fff}.kpi,.card,table,.receipt{box-shadow:none}}
"""

    let private avatar (cls: string) (name: string) =
        sprintf "<span class=\"av %s\">%s</span>" cls (esc (if name = "" then "?" else Char.ToUpperInvariant(name[0]).ToString()))

    /// A single HTML file with inline CSS and SVG; opens offline, prints cleanly.
    let html (version: string) (a: Analysis) (plan: Plan) =
        let cur = a.Ledger.Base
        let m = Money.format cur
        let balances = a.Balances |> List.map (fun b -> b.Person, b.Net)
        let sb = StringBuilder()
        let w (s: string) = sb.Append s |> ignore
        let expenses = a.Entries |> List.filter (fun e -> e.Kind = "expense")
        let perHead = if a.People.IsEmpty then 0L else a.TotalSpend / int64 a.People.Length
        let saved = a.PairwiseDebts.Length - plan.Transfers.Length
        let greedyCount = (Settle.greedy balances).Length
        w "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        w (sprintf "<title>%s · evenup</title>" (esc a.Ledger.Name))
        w "<meta name=\"generator\" content=\"evenup\"><style>"
        w themeCss
        w reportCss
        w "</style></head><body><main class=\"wrap\">"
        // header
        w "<div class=\"eyebrow\">evenup · settlement report</div>"
        w (sprintf "<h1>%s</h1>" (esc a.Ledger.Name))
        w "<div class=\"meta\">"
        let period = dateRange a
        if period <> "" then w (sprintf "<span>%s</span>" (esc period))
        w (sprintf "<span>%d people</span><span>%d expenses</span><span>base currency %s</span></div>" a.People.Length expenses.Length (esc cur))
        // KPIs
        w "<div class=\"kpis\">"
        w (sprintf "<div class=\"kpi\"><div class=\"k\">Total spent</div><div class=\"v\">%s</div></div>" (esc (m a.TotalSpend)))
        w (sprintf "<div class=\"kpi\"><div class=\"k\">Average per person</div><div class=\"v\">%s</div></div>" (esc (m perHead)))
        w (sprintf "<div class=\"kpi\"><div class=\"k\">Direct debts</div><div class=\"v\">%d <small>IOUs</small></div></div>" a.PairwiseDebts.Length)
        w (sprintf "<div class=\"kpi hl\"><div class=\"k\">Transfers to settle</div><div class=\"v\">%d <small>%s</small></div></div>"
                plan.Transfers.Length (if saved > 0 then sprintf "−%d saved" saved else methodName plan.Method))
        w "</div>"
        // settle up
        let methodBadge =
            match plan.Method with
            | Optimal when greedyCount > plan.Transfers.Length ->
                sprintf "<span class=\"badge ok\">proven minimal</span><span class=\"badge\">greedy would need %d</span>" greedyCount
            | Optimal -> "<span class=\"badge ok\">proven minimal</span>"
            | m -> sprintf "<span class=\"badge\">%s</span>" (methodName m)
        w (sprintf "<section><div class=\"head\"><div><h2>Settle up %s</h2><p class=\"sub\">Make these payments and every balance is exactly zero.</p></div></div>" methodBadge)
        w "<div class=\"receipt\">"
        if List.isEmpty plan.Transfers then
            w "<div class=\"square\">Everyone is square — nothing to pay. 🎉</div>"
        for t in plan.Transfers do
            w (sprintf "<div class=\"tx\"><div class=\"who\">%s%s</div><div class=\"arrow\">pays</div><div class=\"who\">%s%s<span class=\"amt\" style=\"margin-left:12px\">%s</span></div></div>"
                    (avatar "neg" t.From) (esc t.From) (avatar "pos" t.To) (esc t.To) (esc (m t.Amount)))
        w "</div></section>"
        // graphs
        w "<section><div class=\"head\"><div><h2>Before &amp; after</h2><p class=\"sub\">Arrows point from the person who owes to the person who is owed. Thicker means more money.</p></div></div><div class=\"graphs\">"
        w (sprintf "<div class=\"card\"><h3>Who owes whom directly <span class=\"badge\">%d debts</span></h3><p>Each share owed back to whoever paid it.</p>%s</div>"
                a.PairwiseDebts.Length (Svg.graph "before" balances a.PairwiseDebts m))
        w (sprintf "<div class=\"card\"><h3>evenup plan <span class=\"badge ok\">%d transfers</span></h3><p>Same balances, fewest possible payments.</p>%s</div>"
                plan.Transfers.Length (Svg.graph "after" balances plan.Transfers m))
        w "</div></section>"
        // balances
        let maxAbs = a.Balances |> List.fold (fun acc b -> max acc (abs b.Net)) 1L
        w "<section><div class=\"head\"><div><h2>Balances</h2><p class=\"sub\">Paid for the group minus own share, adjusted by payments already made.</p></div></div><div class=\"scroll\"><table><thead><tr><th>Person</th><th class=\"n\">Paid</th><th class=\"n\">Share</th><th class=\"n\">Payments</th><th class=\"n\">Net</th><th style=\"width:28%\"></th></tr></thead><tbody>"
        for b in a.Balances do
            let pct = 50.0 * float (abs b.Net) / float maxAbs
            let bar =
                if b.Net >= 0L then sprintf "<i style=\"left:50%%;width:%.1f%%;background:var(--pos)\"></i>" pct
                else sprintf "<i style=\"right:50%%;width:%.1f%%;background:var(--neg)\"></i>" pct
            let cls = if b.Net > 0L then "pos" elif b.Net < 0L then "neg" else ""
            let net = if b.Net > 0L then "+" + m b.Net else m b.Net
            w (sprintf "<tr><td><div class=\"who\">%s%s</div></td><td class=\"n\">%s</td><td class=\"n\">%s</td><td class=\"n muted\">%s</td><td class=\"n %s\">%s</td><td><div class=\"bar\"><span class=\"mid\"></span>%s</div></td></tr>"
                    (avatar (if b.Net >= 0L then "pos" else "neg") b.Person) (esc b.Person) (esc (m b.Paid)) (esc (m b.Share))
                    (esc (m (b.Sent - b.Received))) cls (esc net) (bar.Replace(",", ".")))
        w "</tbody></table></div></section>"
        // ledger
        w "<section><div class=\"head\"><div><h2>Ledger</h2><p class=\"sub\">Every entry after its split rule and currency conversion.</p></div></div><div class=\"scroll\"><table><thead><tr><th>Date</th><th>What</th><th>Paid by</th><th>Split</th><th class=\"n\">Amount</th></tr></thead><tbody>"
        for e in a.Entries do
            let paidBy = e.Paid |> List.map fst |> String.concat ", "
            let original =
                if e.Currency <> cur then sprintf "<div class=\"muted\" style=\"font-size:12px\">%s</div>" (esc (Money.format e.Currency e.Amount)) else ""
            let among = e.Owed |> List.filter (fun (_, v) -> v <> 0L) |> List.map fst |> String.concat ", "
            w (sprintf "<tr><td class=\"muted\" style=\"white-space:nowrap\">%s</td><td><strong>%s</strong><div class=\"muted\" style=\"font-size:12.5px\">%s</div></td><td>%s</td><td><span class=\"chip\">%s</span></td><td class=\"n\">%s%s</td></tr>"
                    (esc (dateText e.Date)) (esc e.Title) (esc (if e.Kind = "payment" then "direct payment" else "for " + among))
                    (esc paidBy) (esc e.SplitKind) (esc (m e.BaseAmount)) original)
        w "</tbody></table></div></section>"
        w (sprintf "<footer><span>Generated by <a href=\"https://github.com/ardazeybek-dev/evenup\">evenup</a> %s · amounts in %s minor units, no floating point</span><span>%s</span></footer>"
                (esc version) (esc cur) (DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", inv)))
        w "</main></body></html>"
        sb.ToString()
