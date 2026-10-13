module Evenup.Tests.EngineTests

open Xunit
open Evenup
open Evenup.Tests.ParserTests

let analyze text =
    match parseOk text |> Engine.analyze with
    | Ok a -> a
    | Error ds -> failwithf "unexpected errors: %A" ds

let analyzeErrors text =
    match parseOk text |> Engine.analyze with
    | Ok _ -> failwith "expected errors"
    | Error ds -> ds

let net (a: Analysis) =
    a.Balances |> List.map (fun b -> b.Person, b.Net) |> Map.ofList

[<Fact>]
let ``equal split of an odd amount loses no cent`` () =
    let a = analyze "currency EUR\npeople a b c\na paid 100\n"
    let n = net a
    Assert.Equal(6666L, n["a"]) // paid 100.00, share 33.34
    Assert.Equal(-3333L, n["b"])
    Assert.Equal(-3333L, n["c"])
    Assert.Equal(0L, a.Balances |> List.sumBy (fun b -> b.Net))

[<Fact>]
let ``shares and percent follow their weights`` () =
    let a = analyze "people a b\na paid 90 split shares a:1 b:2\nb paid 50 split percent a:20 b:80\n"
    let n = net a
    // a: paid 90, share 30 + 10 -> +50 ; b: paid 50, share 60 + 40 -> -50
    Assert.Equal(5000L, n["a"])
    Assert.Equal(-5000L, n["b"])

[<Fact>]
let ``itemized spreads tax and tip by subtotal`` () =
    // items 60 (a 40, b 20) + 15 tip -> a pays 40 + 10, b pays 20 + 5
    let a = analyze "people a b\nb paid 75 itemized\n  a 40\n  b 20\n"
    let e = a.Entries.Head
    Assert.Equal<(Person * int64) list>([ "a", 5000L; "b", 2500L ], e.Owed)

[<Fact>]
let ``shared receipt lines are split equally inside the item`` () =
    let a = analyze "people a b c\na paid 30 itemized\n  b c 30\n"
    Assert.Equal<(Person * int64) list>([ "b", 1500L; "c", 1500L ], a.Entries.Head.Owed)

[<Fact>]
let ``foreign currency is converted once and stays zero-sum`` () =
    let a = analyze "currency EUR\nrate USD 0.92\npeople a b c\na paid 10 USD\n"
    let e = a.Entries.Head
    Assert.Equal(920L, e.BaseAmount)
    Assert.Equal(920L, e.Owed |> List.sumBy snd)
    Assert.Equal(0L, a.Balances |> List.sumBy (fun b -> b.Net))

[<Fact>]
let ``direct payments move balances`` () =
    let a = analyze "people a b\na paid 100\nb paid a 50\n"
    Assert.True(a.Balances |> List.forall (fun b -> b.Net = 0L))
    Assert.Empty(a.PairwiseDebts)

[<Fact>]
let ``refunds (negative expenses) reverse the split`` () =
    let a = analyze "people a b\na paid 100\na paid -20\n"
    Assert.Equal(4000L, (net a)["a"])

[<Fact>]
let ``pairwise debts cancel opposite directions`` () =
    let a = analyze "people a b\na paid 100\nb paid 60\n"
    // b owes a 50, a owes b 30 -> b owes a 20
    Assert.Equal<Transfer list>([ { From = "b"; To = "a"; Amount = 2000L } ], a.PairwiseDebts)

[<Fact>]
let ``typos in names get a suggestion`` () =
    let ds = analyzeErrors "people alice bob\nalice paid 10 for bbo\n"
    Assert.Contains("did you mean 'bob'", ds.Head.Message)
    Assert.Equal(2, ds.Head.Line)

[<Fact>]
let ``split rules are validated`` () =
    Assert.Contains("not 100", (analyzeErrors "people a b\na paid 10 split percent a:50 b:40").Head.Message)
    Assert.Contains("add up to", (analyzeErrors "people a b\na paid 10 split exact a:5 b:4").Head.Message)
    Assert.Contains("payers cover", (analyzeErrors "people a b\na:5 b:4 paid 10").Head.Message)
    Assert.Contains("no exchange rate", (analyzeErrors "currency EUR\npeople a b\na paid 10 GBP").Head.Message)
    Assert.Contains("appears twice", (analyzeErrors "people a b\na paid 10 for a a").Head.Message)

[<Fact>]
let ``people are inferred when not declared`` () =
    let a = analyze "alice paid 30 for bob carol\n"
    Assert.Equal<Person list>([ "alice"; "bob"; "carol" ], a.People)
