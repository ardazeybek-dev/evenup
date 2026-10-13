module Evenup.Tests.ParserTests

open System
open Xunit
open Evenup

let parseOk text =
    match Parser.parse text with
    | Ok l -> l
    | Error ds -> failwithf "unexpected errors: %A" ds

let parseErrors text =
    match Parser.parse text with
    | Ok _ -> failwith "expected errors"
    | Error ds -> ds

let expenses (l: Ledger) =
    l.Entries |> List.choose (function ExpenseEntry e -> Some e | _ -> None)

[<Fact>]
let ``reads directives`` () =
    let l = parseOk "group \"Lisbon 2026\"\ncurrency EUR\nrate USD 0.92\npeople alice bob carol\n"
    Assert.Equal("Lisbon 2026", l.Name)
    Assert.Equal("EUR", l.Base)
    Assert.Equal(0.92m, l.Rates["USD"])
    Assert.Equal<Person list>([ "alice"; "bob"; "carol" ], l.People)

[<Fact>]
let ``reads a simple expense split with everyone`` () =
    let l = parseOk "currency EUR\npeople a b\n2026-05-01 \"Airbnb\" a paid 840\n"
    let e = expenses l |> List.exactlyOne
    Assert.Equal(Some(DateOnly(2026, 5, 1)), e.Date)
    Assert.Equal("Airbnb", e.Title)
    Assert.Equal(One "a", e.Payers)
    Assert.Equal(84000L, e.Amount)
    Assert.Equal(Equal [], e.Split)

[<Fact>]
let ``reads every split kind`` () =
    let text =
        """
currency EUR
people a b c
a paid 30 for b c
a paid 30 split equally among a b
a paid 40 split shares a:2 b:1 c:1
a paid 40 split percent a:50 b:25% c : 25
a paid 40 split exact a:10 b: 20.5 c:9.50
a paid 50 USD
"""
    let es = parseOk text |> expenses
    Assert.Equal(Equal [ "b"; "c" ], es[0].Split)
    Assert.Equal(Equal [ "a"; "b" ], es[1].Split)
    Assert.Equal(Shares [ "a", 2m; "b", 1m; "c", 1m ], es[2].Split)
    Assert.Equal(Percent [ "a", 50m; "b", 25m; "c", 25m ], es[3].Split)
    Assert.Equal(Exact [ "a", 1000L; "b", 2050L; "c", 950L ], es[4].Split)
    Assert.Equal("USD", es[5].Currency)

[<Fact>]
let ``reads multiple payers`` () =
    let e = parseOk "people a b\na:50, b:30 paid 80" |> expenses |> List.exactlyOne
    Assert.Equal(Many [ "a", 5000L; "b", 3000L ], e.Payers)

[<Fact>]
let ``reads itemized receipts with indented lines`` () =
    let text =
        """
people a b c
"Pizza" b paid 74.80 itemized
  "Margherita" a 18.50
  c 21
  b, c 22.00
a paid 10
"""
    let es = parseOk text |> expenses
    match es[0].Split with
    | Itemized items ->
        Assert.Equal(3, items.Length)
        Assert.Equal("Margherita", items[0].Label)
        Assert.Equal<Person list>([ "b"; "c" ], items[2].People)
        Assert.Equal(2200L, items[2].Amount)
    | other -> failwithf "expected itemized, got %A" other
    Assert.Equal(2, es.Length)

[<Fact>]
let ``reads a direct payment`` () =
    let l = parseOk "currency EUR\npeople a b\n2026-05-04 b paid a 25.50\n"
    match l.Entries with
    | [ PaymentEntry p ] ->
        Assert.Equal("b", p.From)
        Assert.Equal("a", p.To)
        Assert.Equal(2550L, p.Amount)
    | other -> failwithf "expected a payment, got %A" other

[<Fact>]
let ``comments and blank lines are ignored`` () =
    let l = parseOk "# trip\n\npeople a b # the crew\na paid 10 # lunch\n"
    Assert.Equal(1, l.Entries.Length)

[<Fact>]
let ``keywords are case-insensitive without culture tricks`` () =
    let previous = Threading.Thread.CurrentThread.CurrentCulture
    try
        Threading.Thread.CurrentThread.CurrentCulture <- Globalization.CultureInfo "tr-TR"
        // "SPLIT" and "ITEMIZED" contain the letter I, which tr-TR lowercases to 'ı'
        let l = parseOk "PEOPLE a b\na PAID 10 SPLIT SHARES a:1 b:1\n"
        Assert.Equal(1, l.Entries.Length)
    finally
        Threading.Thread.CurrentThread.CurrentCulture <- previous

[<Fact>]
let ``reports every bad line with line and column`` () =
    let ds = parseErrors "people a b\na paid 12.345\nb paid a\nwhat is this\n"
    Assert.Equal(3, ds.Length)
    Assert.Equal(2, ds[0].Line)
    Assert.Equal(8, ds[0].Column)
    Assert.Equal(3, ds[1].Line)
    Assert.Equal(4, ds[2].Line)

[<Fact>]
let ``rejects bad dates, unknown splits and unterminated strings`` () =
    Assert.Contains("date", (parseErrors "2026-13-01 a paid 10").Head.Message)
    Assert.Contains("unknown split", (parseErrors "a paid 10 split evenly").Head.Message)
    Assert.Contains("unterminated", (parseErrors "\"Dinner a paid 10").Head.Message)

[<Fact>]
let ``rejects currency after the first expense`` () =
    Assert.Contains("before the first expense", (parseErrors "a paid 10\ncurrency EUR").Head.Message)

[<Fact>]
let ``itemized without items is an error`` () =
    let ds = parseErrors "people a b\na paid 10 itemized\nb paid 5\n"
    Assert.Equal(2, ds.Head.Line)

[<Fact>]
let ``unicode names are allowed`` () =
    let l = parseOk "people ayşe zoë İlker\nayşe paid 10\n"
    Assert.Equal<Person list>([ "ayşe"; "zoë"; "İlker" ], l.People)
