namespace Evenup

open System.Numerics

type SettleMethod =
    /// Provably minimal number of transfers (subset dynamic programming).
    | Optimal
    /// Largest creditor meets largest debtor; at most n − 1 transfers.
    | Greedy
    /// Exact-opposite pairs first, then optimal on what is left if small enough,
    /// otherwise greedy.
    | Hybrid

type Plan =
    { Transfers: Transfer list
      Method: SettleMethod
      /// Zero-sum groups that settle among themselves (only for Optimal).
      Groups: Person list list
      /// People with a non-zero balance.
      Participants: int }

/// Turning balances into the fewest possible transfers.
///
/// With n people who are owed or owe money, n − 1 transfers always suffice.
/// Every subset of people whose balances add up to exactly zero can settle on
/// its own and saves one transfer, so the minimum is n − k where k is the largest
/// number of disjoint zero-sum groups. Finding k is NP-hard (it contains subset
/// sum), so the exact solver is exponential: O(2^n · n) time, O(2^n) memory.
module Settle =

    /// Default largest n solved exactly: 2^20 states ≈ 9 MB and well under a second.
    [<Literal>]
    let DefaultExactLimit = 20

    let private nonZero (balances: (Person * int64) list) =
        balances |> List.filter (fun (_, v) -> v <> 0L) |> List.sortBy fst

    /// Repeatedly settles the largest creditor against the largest debtor.
    let greedy (balances: (Person * int64) list) : Transfer list =
        let ledger = nonZero balances |> List.map (fun (p, v) -> p, ref v)
        let mutable transfers = []
        let mutable fin = false
        while not fin do
            let creditors = ledger |> List.filter (fun (_, v) -> v.Value > 0L)
            let debtors = ledger |> List.filter (fun (_, v) -> v.Value < 0L)
            if List.isEmpty creditors || List.isEmpty debtors then
                fin <- true
            else
                // maxBy/minBy keep the first of equals; input is sorted by name, so this is deterministic
                let cp, cv = creditors |> List.maxBy (fun (_, v) -> v.Value)
                let dp, dv = debtors |> List.minBy (fun (_, v) -> v.Value)
                let amount = min cv.Value (-dv.Value)
                transfers <- { From = dp; To = cp; Amount = amount } :: transfers
                cv.Value <- cv.Value - amount
                dv.Value <- dv.Value + amount
        List.rev transfers

    /// Largest partition of `values` into zero-sum groups, as index lists.
    /// dp[mask] = max over i in mask of dp[mask \ {i}], plus one when mask sums to zero:
    /// the best ordering of removals hits a zero-sum prefix as often as possible.
    let zeroSumGroups (values: int64[]) : int list list =
        let n = values.Length
        if n = 0 then
            []
        else
            let size = 1 <<< n
            let sums = Array.zeroCreate<int64> size
            let dp = Array.zeroCreate<byte> size
            for mask in 1 .. size - 1 do
                let low = BitOperations.TrailingZeroCount mask
                sums[mask] <- sums[mask &&& (mask - 1)] + values[low]
                let mutable best = 0uy
                let mutable rest = mask
                while rest <> 0 do
                    let i = BitOperations.TrailingZeroCount rest
                    rest <- rest &&& (rest - 1)
                    let c = dp[mask ^^^ (1 <<< i)]
                    if c > best then best <- c
                dp[mask] <- if sums[mask] = 0L then best + 1uy else best
            // walk back from the full set; a zero-sum mask closes a group
            let groups = ResizeArray<int list>()
            let mutable current = []
            let mutable mask = size - 1
            while mask <> 0 do
                let target = if sums[mask] = 0L then dp[mask] - 1uy else dp[mask]
                let mutable rest = mask
                let mutable chosen = -1
                while chosen < 0 do
                    let i = BitOperations.TrailingZeroCount rest
                    rest <- rest &&& (rest - 1)
                    if dp[mask ^^^ (1 <<< i)] = target then chosen <- i
                current <- chosen :: current
                mask <- mask ^^^ (1 <<< chosen)
                if sums[mask] = 0L then
                    groups.Add(List.sort current)
                    current <- []
            List.ofSeq groups

    /// The provably minimal plan. Throws if there are more than 30 people with a balance.
    let optimal (balances: (Person * int64) list) : Plan =
        let nz = nonZero balances |> Array.ofList
        if nz.Length > 30 then invalidArg (nameof balances) "too many people for the exact solver"
        let groups = zeroSumGroups (nz |> Array.map snd)
        let transfers =
            groups |> List.collect (fun g -> greedy [ for i in g -> nz[i] ])
        { Transfers = transfers
          Method = Optimal
          Groups = groups |> List.map (List.map (fun i -> fst nz[i]))
          Participants = nz.Length }

    /// Exact solver when the instance is small enough, a bounded hybrid otherwise.
    let plan (exactLimit: int) (balances: (Person * int64) list) : Plan =
        let nz = nonZero balances
        if nz.Length <= exactLimit then
            optimal nz
        else
            // pair off people whose balances cancel exactly — always part of some optimum
            let remaining = ResizeArray(nz)
            let pairs = ResizeArray<Transfer>()
            let mutable i = 0
            while i < remaining.Count do
                let p, v = remaining[i]
                match Seq.tryFindIndex (fun (_, w) -> w = -v) remaining with
                | Some j when j <> i ->
                    let q, _ = remaining[j]
                    pairs.Add(if v < 0L then { From = p; To = q; Amount = -v } else { From = q; To = p; Amount = v })
                    remaining.RemoveAt(max i j)
                    remaining.RemoveAt(min i j)
                | _ -> i <- i + 1
            let rest = List.ofSeq remaining
            let tail = if rest.Length <= exactLimit then (optimal rest).Transfers else greedy rest
            { Transfers = List.ofSeq pairs @ tail
              Method = Hybrid
              Groups = []
              Participants = nz.Length }

    /// Transfers needed if nobody simplifies anything (one per pairwise debt).
    let naiveCount (analysis: Analysis) = analysis.PairwiseDebts.Length

    let balancesOf (analysis: Analysis) =
        analysis.Balances |> List.map (fun b -> b.Person, b.Net)
