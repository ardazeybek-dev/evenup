module Evenup.Tests.SettleTests

open Xunit
open FsCheck.Xunit
open Evenup

let private apply (balances: (Person * int64) list) (transfers: Transfer list) =
    let mutable m = Map.ofList balances
    for t in transfers do
        m <- m.Add(t.From, m[t.From] + t.Amount).Add(t.To, m[t.To] - t.Amount)
    m

let private settles balances transfers =
    apply balances transfers |> Map.forall (fun _ v -> v = 0L)
    && transfers |> List.forall (fun t -> t.Amount > 0L && t.From <> t.To)

/// Turns arbitrary integers into a zero-sum balance sheet of up to `maxPeople` people.
let private sheet (maxPeople: int) (xs: int list) =
    let vs = xs |> List.truncate (maxPeople - 1) |> List.map (fun x -> int64 (x % 10_000))
    let vs = vs @ [ -(List.sum vs) ]
    vs |> List.mapi (fun i v -> sprintf "p%02d" i, v)

/// Smallest possible number of transfers by brute force over all partitions.
let rec private maxZeroGroups (values: int64 list) =
    match values |> List.filter ((<>) 0L) with
    | [] -> 0
    | first :: rest ->
        // the group containing `first`: try every subset of the rest
        let n = rest.Length
        seq {
            for mask in 0 .. (1 <<< n) - 1 do
                let inGroup = [ for i in 0 .. n - 1 do if mask &&& (1 <<< i) <> 0 then rest[i] ]
                if first + List.sum inGroup = 0L then
                    let outside = [ for i in 0 .. n - 1 do if mask &&& (1 <<< i) = 0 then rest[i] ]
                    yield 1 + maxZeroGroups outside
        }
        |> Seq.fold max 0

[<Fact>]
let ``greedy is beaten when two groups can settle separately`` () =
    // {+5,-3,-2} and {+4,-4} settle in 2 + 1 = 3 transfers; greedy needs 4
    let balances = [ "a", 500L; "b", -300L; "c", -200L; "d", 400L; "e", -400L ]
    let g = Settle.greedy balances
    let o = Settle.optimal balances
    Assert.Equal(4, g.Length)
    Assert.Equal(3, o.Transfers.Length)
    Assert.Equal(Optimal, o.Method)
    Assert.Equal(2, o.Groups.Length)
    Assert.True(settles balances o.Transfers)

[<Fact>]
let ``nothing to do when everyone is square`` () =
    let p = Settle.plan 20 [ "a", 0L; "b", 0L ]
    Assert.Empty(p.Transfers)

[<Fact>]
let ``hybrid kicks in above the exact limit and still settles`` () =
    let balances = [ for i in 1..15 -> sprintf "c%02d" i, int64 i ] @ [ for i in 1..15 -> sprintf "d%02d" i, -(int64 i) ]
    let p = Settle.plan 10 balances
    Assert.Equal(Hybrid, p.Method)
    Assert.Equal(15, p.Transfers.Length) // every pair cancels exactly
    Assert.True(settles balances p.Transfers)

[<Fact>]
let ``exact solver handles twenty people quickly`` () =
    let balances = sheet 20 [ 120; -45; 77; -300; 18; 64; -9; 250; -130; 41; -77; 19; 300; -64; 5; -250; 33; 130; -45 ]
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let p = Settle.optimal balances
    Assert.True(settles balances p.Transfers)
    Assert.True(sw.ElapsedMilliseconds < 10_000L)

[<Property>]
let ``every plan settles every balance`` (xs: int list) =
    let b = sheet 14 xs
    settles b (Settle.greedy b) && settles b (Settle.plan 12 b).Transfers

[<Property>]
let ``never more than n - 1 transfers`` (xs: int list) =
    let b = sheet 14 xs
    let n = b |> List.filter (fun (_, v) -> v <> 0L) |> List.length
    let p = Settle.plan 14 b
    p.Transfers.Length <= max 0 (n - 1)

[<Property>]
let ``optimal never needs more transfers than greedy`` (xs: int list) =
    let b = sheet 14 xs
    (Settle.optimal b).Transfers.Length <= (Settle.greedy b).Length

[<Property(MaxTest = 200)>]
let ``optimal matches brute force on small instances`` (xs: sbyte list) =
    // small values make zero-sum subsets common, which is the interesting case
    let b = xs |> List.map (fun x -> int x % 6) |> sheet 9
    let values = b |> List.map snd
    let n = values |> List.filter ((<>) 0L) |> List.length
    (Settle.optimal b).Transfers.Length = n - maxZeroGroups values
