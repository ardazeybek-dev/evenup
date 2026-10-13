module Evenup.Terminal

open System
open System.Globalization

/// ANSI colours, switched off for pipes and when NO_COLOR is set (https://no-color.org).
let mutable colors =
    not Console.IsOutputRedirected && String.IsNullOrEmpty(Environment.GetEnvironmentVariable "NO_COLOR")

let private paint code (s: string) = if colors then sprintf "\u001b[%sm%s\u001b[0m" code s else s
let bold = paint "1"
let dim = paint "2"
let green = paint "32"
let red = paint "31"
let cyan = paint "36"
let yellow = paint "33"

/// Width on screen, ignoring escape codes. Good enough for names and amounts.
let private visible (s: string) =
    let mutable n = 0
    let mutable inEsc = false
    for c in s do
        if c = '\u001b' then inEsc <- true
        elif inEsc then (if c = 'm' then inEsc <- false)
        else n <- n + 1
    n

let padRight w (s: string) = s + String(' ', max 0 (w - visible s))
let padLeft w (s: string) = String(' ', max 0 (w - visible s)) + s

let private signed (cur: string) (v: int64) =
    let text = if v > 0L then "+" + Money.format cur v else Money.format cur v
    if v > 0L then green text elif v < 0L then red text else dim text

/// The human-readable output of `evenup settle`.
let printSettlement (a: Analysis) (plan: Plan) =
    let cur = a.Ledger.Base
    let m = Money.format cur
    let expenses = a.Entries |> List.filter (fun e -> e.Kind = "expense") |> List.length
    printfn ""
    printfn "  %s" (bold a.Ledger.Name)
    let period = Report.dateRange a
    printfn "  %s" (dim (String.concat " · " ([ period; sprintf "%d people" a.People.Length; sprintf "%d expenses" expenses; "total " + m a.TotalSpend ] |> List.filter ((<>) ""))))
    printfn ""
    let methodText =
        match plan.Method with
        | Optimal ->
            let g = (Settle.greedy (Settle.balancesOf a)).Length
            if g > plan.Transfers.Length then green "optimal" + dim (sprintf " (greedy would need %d)" g) else green "optimal"
        | Greedy -> yellow "greedy"
        | Hybrid -> yellow "hybrid"
    printfn "  %s  %s" (bold "SETTLE UP") (dim (sprintf "%d transfer(s) instead of %d direct debts ·" plan.Transfers.Length a.PairwiseDebts.Length) + " " + methodText)
    if List.isEmpty plan.Transfers then
        printfn "  %s" (green "Everyone is square.")
    else
        let w = plan.Transfers |> List.collect (fun t -> [ t.From.Length; t.To.Length ]) |> List.max
        let aw = plan.Transfers |> List.map (fun t -> (m t.Amount).Length) |> List.max
        for t in plan.Transfers do
            printfn "    %s %s %s  %s" (padRight w (red t.From)) (dim "→") (padRight w (green t.To)) (padLeft aw (bold (m t.Amount)))
    printfn ""
    printfn "  %s" (bold "BALANCES")
    let nameW = a.Balances |> List.map (fun b -> b.Person.Length) |> List.fold max 6
    let col (vals: int64 list) title = vals |> List.map (fun v -> (m v).Length + 1) |> List.fold max (String.length title)
    let pw = col (a.Balances |> List.map (fun b -> b.Paid)) "paid"
    let sw = col (a.Balances |> List.map (fun b -> b.Share)) "share"
    let yw = col (a.Balances |> List.map (fun b -> b.Sent - b.Received)) "payments"
    let nw = col (a.Balances |> List.map (fun b -> b.Net)) "net"
    printfn "    %s  %s  %s  %s  %s" (padRight nameW (dim "person")) (padLeft pw (dim "paid")) (padLeft sw (dim "share")) (padLeft yw (dim "payments")) (padLeft nw (dim "net"))
    for b in a.Balances do
        printfn "    %s  %s  %s  %s  %s" (padRight nameW b.Person) (padLeft pw (m b.Paid)) (padLeft sw (m b.Share)) (padLeft yw (dim (m (b.Sent - b.Received)))) (padLeft nw (signed cur b.Net))
    printfn ""

/// Compiler-style diagnostics with the offending source line and a caret.
let printDiagnostics (file: string) (source: string) (diags: Diagnostic list) =
    let lines = source.Replace("\r\n", "\n").Split '\n'
    for d in diags do
        eprintfn "%s %s" (red (bold "error:")) (bold (sprintf "%s:%d:%d" file d.Line d.Column) + " " + d.Message)
        if d.Line >= 1 && d.Line <= lines.Length then
            let gutter = string d.Line
            eprintfn "  %s %s %s" (dim gutter) (dim "|") lines[d.Line - 1]
            eprintfn "  %s %s %s%s" (String(' ', gutter.Length)) (dim "|") (String(' ', max 0 (d.Column - 1))) (red "^")
    eprintfn "%s" (red (sprintf "%d error(s)" diags.Length))

let invariant (x: int) = x.ToString(CultureInfo.InvariantCulture)
