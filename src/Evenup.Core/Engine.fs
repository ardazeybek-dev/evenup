namespace Evenup

open System
open System.Collections.Generic

/// One ledger line after its split rule has been applied and its amounts have
/// been converted to the base currency. `Paid` and `Owed` both add up to `BaseAmount`.
type ResolvedEntry =
    { Line: int
      Date: DateOnly option
      Title: string
      Kind: string
      SplitKind: string
      Amount: int64
      Currency: string
      BaseAmount: int64
      Paid: (Person * int64) list
      Owed: (Person * int64) list }

/// Where a person stands. `Net` > 0 means the group owes them money.
type PersonSummary =
    { Person: Person
      Paid: int64
      Share: int64
      Sent: int64
      Received: int64
      Net: int64 }

type Analysis =
    { Ledger: Ledger
      People: Person list
      Entries: ResolvedEntry list
      Balances: PersonSummary list
      /// Who owes whom if everyone simply paid back each payer directly.
      PairwiseDebts: Transfer list
      TotalSpend: int64 }

module Engine =

    let private levenshtein (a: string) (b: string) =
        let d = Array2D.init (a.Length + 1) (b.Length + 1) (fun i j -> if i = 0 then j elif j = 0 then i else 0)
        for i in 1 .. a.Length do
            for j in 1 .. b.Length do
                let cost = if a[i - 1] = b[j - 1] then 0 else 1
                d[i, j] <- min (min (d[i - 1, j] + 1) (d[i, j - 1] + 1)) (d[i - 1, j - 1] + cost)
        d[a.Length, b.Length]

    /// "unknown person 'bbo' — did you mean 'bob'?"
    let private unknownPerson (known: Person list) (name: string) =
        let hint =
            known
            |> List.map (fun k -> k, levenshtein (name.ToLowerInvariant()) (k.ToLowerInvariant()))
            |> List.filter (fun (_, d) -> d <= 2)
            |> List.sortBy snd
            |> List.tryHead
        match hint with
        | Some(k, _) -> sprintf "unknown person '%s' — did you mean '%s'?" name k
        | None -> sprintf "unknown person '%s' (add them to the 'people' line)" name

    let private splitKind =
        function
        | Equal [] -> "equal (everyone)"
        | Equal _ -> "equal"
        | Shares _ -> "shares"
        | Percent _ -> "percent"
        | Exact _ -> "exact"
        | Itemized _ -> "itemized"

    let private noDuplicates (what: string) (names: Person list) =
        match names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
        | Some(dup, _) -> Error(sprintf "'%s' appears twice in the %s" dup what)
        | None -> Ok names

    let private sequence (xs: Result<'a, string> list) : Result<'a list, string> =
        let folder acc x =
            match acc, x with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok xs, Ok v -> Ok(v :: xs)
        xs |> List.fold folder (Ok []) |> Result.map List.rev

    /// Sums amounts per person, keeping first-appearance order.
    let private merge (parts: (Person * int64) seq) =
        let order = ResizeArray<Person>()
        let acc = Dictionary<Person, int64>()
        for p, v in parts do
            match acc.TryGetValue p with
            | true, cur -> acc[p] <- cur + v
            | _ ->
                order.Add p
                acc[p] <- v
        [ for p in order -> p, acc[p] ]

    /// Applies a split rule in the expense's own currency.
    let resolveShares (everyone: Person list) (e: Expense) : Result<(Person * int64) list, string> =
        let total = e.Amount
        let fmt = Money.format e.Currency
        let equally (people: Person list) amount =
            let parts = Money.allocateBy amount (people |> List.map (fun _ -> 1L) |> Array.ofList)
            List.zip people (List.ofArray parts)
        match e.Split with
        | Equal among ->
            let people = if List.isEmpty among then everyone else among
            if List.isEmpty people then Error "nobody to split with — add a 'people' line"
            else noDuplicates "split" people |> Result.map (fun ps -> equally ps total)
        | Shares xs ->
            noDuplicates "split" (List.map fst xs)
            |> Result.bind (fun _ -> xs |> List.map (snd >> Money.weightOf) |> sequence)
            |> Result.bind (fun ws ->
                if ws |> List.forall (fun w -> w.IsZero) then Error "all shares are zero"
                else Ok(List.zip (List.map fst xs) (List.ofArray (Money.allocate total (Array.ofList ws)))))
        | Percent xs ->
            let sum = xs |> List.sumBy snd
            if sum <> 100m then
                Error(sprintf "percentages add up to %s, not 100" (sum.ToString(Globalization.CultureInfo.InvariantCulture)))
            else
                noDuplicates "split" (List.map fst xs)
                |> Result.bind (fun _ -> xs |> List.map (snd >> Money.weightOf) |> sequence)
                |> Result.map (fun ws -> List.zip (List.map fst xs) (List.ofArray (Money.allocate total (Array.ofList ws))))
        | Exact xs ->
            let sum = xs |> List.sumBy snd
            if sum <> total then
                Error(sprintf "exact amounts add up to %s but the expense is %s" (fmt sum) (fmt total))
            elif xs |> List.exists (fun (_, v) -> v <> 0L && sign v <> sign total) then
                Error "exact amounts must all have the same sign as the total"
            else
                noDuplicates "split" (List.map fst xs) |> Result.map (fun _ -> xs)
        | Itemized items ->
            let itemSum = items |> List.sumBy (fun i -> i.Amount)
            let extra = total - itemSum
            if total < 0L then Error "an itemized expense cannot be negative"
            elif itemSum = 0L then Error "the items add up to zero"
            elif extra < 0L && -extra > itemSum then Error "the discount is larger than the items"
            else
                items
                |> List.map (fun i -> noDuplicates "item" i.People)
                |> sequence
                |> Result.map (fun _ ->
                    let subtotals = items |> Seq.collect (fun i -> equally i.People i.Amount) |> merge
                    if extra = 0L then
                        subtotals
                    else
                        // tax, tip or discount follows each person's subtotal
                        let spread = Money.allocateBy extra (subtotals |> List.map snd |> Array.ofList)
                        List.map2 (fun (p, s) x -> p, s + x) subtotals (List.ofArray spread))

    let private resolvePayers (e: Expense) =
        match e.Payers with
        | One p -> Ok [ p, e.Amount ]
        | Many xs ->
            let sum = xs |> List.sumBy snd
            if sum <> e.Amount then
                Error(sprintf "payers cover %s but the expense is %s" (Money.format e.Currency sum) (Money.format e.Currency e.Amount))
            else
                noDuplicates "payers" (List.map fst xs) |> Result.map (fun _ -> xs)

    /// Converts a zero-sum set of parts from the entry currency to the base currency
    /// without breaking the sum: the total is converted once, then re-allocated.
    let private toBase (ledger: Ledger) (currency: string) (total: int64) (parts: (Person * int64) list) =
        if currency = ledger.Base then
            Ok(total, parts)
        else
            match ledger.Rates.TryFind currency with
            | None -> Error(sprintf "no exchange rate for %s — add 'rate %s <value in %s>'" currency currency ledger.Base)
            | Some rate ->
                let baseTotal = Money.convert currency ledger.Base rate total
                let alloc = Money.allocateBy baseTotal (parts |> List.map snd |> Array.ofList)
                Ok(baseTotal, List.zip (List.map fst parts) (List.ofArray alloc))

    let private namesIn (entry: Entry) =
        match entry with
        | PaymentEntry p -> [ p.From; p.To ]
        | ExpenseEntry e ->
            let payers = match e.Payers with One p -> [ p ] | Many xs -> List.map fst xs
            let split =
                match e.Split with
                | Equal xs -> xs
                | Shares xs
                | Percent xs -> List.map fst xs
                | Exact xs -> List.map fst xs
                | Itemized items -> items |> List.collect (fun i -> i.People)
            payers @ split

    let private peopleOf (ledger: Ledger) =
        if not (List.isEmpty ledger.People) then ledger.People
        else ledger.Entries |> List.collect namesIn |> List.distinct

    let private resolveEntry (ledger: Ledger) (people: Person list) (entry: Entry) : Result<ResolvedEntry, string> =
        match entry with
        | PaymentEntry p ->
            toBase ledger p.Currency p.Amount [ p.From, p.Amount ]
            |> Result.map (fun (b, _) ->
                { Line = p.Line
                  Date = p.Date
                  Title = sprintf "%s paid %s" p.From p.To
                  Kind = "payment"
                  SplitKind = "payment"
                  Amount = p.Amount
                  Currency = p.Currency
                  BaseAmount = b
                  Paid = [ p.From, b ]
                  Owed = [ p.To, b ] })
        | ExpenseEntry e ->
            resolvePayers e
            |> Result.bind (fun payers ->
                resolveShares people e
                |> Result.bind (fun owed ->
                    toBase ledger e.Currency e.Amount payers
                    |> Result.bind (fun (b, paidBase) ->
                        toBase ledger e.Currency e.Amount owed
                        |> Result.map (fun (_, owedBase) ->
                            { Line = e.Line
                              Date = e.Date
                              Title = if e.Title = "" then sprintf "Expense on line %d" e.Line else e.Title
                              Kind = "expense"
                              SplitKind = splitKind e.Split
                              Amount = e.Amount
                              Currency = e.Currency
                              BaseAmount = b
                              Paid = paidBase
                              Owed = owedBase }))))

    /// Direct debts before any simplification: every share is owed to the payer(s)
    /// of that expense, then opposite directions between two people cancel out.
    let pairwiseDebts (people: Person list) (entries: ResolvedEntry list) : Transfer list =
        let rank = people |> List.mapi (fun i p -> p, i) |> dict
        let debt = Dictionary<struct (Person * Person), int64>()
        let add (debtor: Person) (creditor: Person) (v: int64) =
            if debtor <> creditor && v <> 0L then
                // key is (lower, higher) by rank; positive = lower owes higher
                let a, b, s = if rank[debtor] < rank[creditor] then debtor, creditor, 1L else creditor, debtor, -1L
                let key = struct (a, b)
                match debt.TryGetValue key with
                | true, cur -> debt[key] <- cur + s * v
                | _ -> debt[key] <- s * v
        for e in entries do
            if e.Kind = "payment" then
                let from, _ = e.Paid.Head
                let target, _ = e.Owed.Head
                add target from e.BaseAmount
            else
                let weights = e.Paid |> List.map snd |> Array.ofList
                for debtor, owed in e.Owed do
                    if owed <> 0L then
                        let parts = Money.allocateBy owed weights
                        List.iter2 (fun (payer, _) part -> add debtor payer part) e.Paid (List.ofArray parts)
        [ for KeyValue(struct (a, b), v) in debt do
              if v > 0L then yield { From = a; To = b; Amount = v }
              elif v < 0L then yield { From = b; To = a; Amount = -v } ]
        |> List.sortBy (fun t -> rank[t.From], rank[t.To])

    /// Validates names, applies every split, converts currencies and computes balances.
    let analyze (ledger: Ledger) : Result<Analysis, Diagnostic list> =
        let people = peopleOf ledger
        let known = set people
        let diags = ResizeArray<Diagnostic>()
        let resolved = ResizeArray<ResolvedEntry>()
        for entry in ledger.Entries do
            let line = match entry with ExpenseEntry e -> e.Line | PaymentEntry p -> p.Line
            match namesIn entry |> List.tryFind (fun n -> not (known.Contains n)) with
            | Some unknown -> diags.Add { Line = line; Column = 1; Message = unknownPerson people unknown }
            | None ->
                match resolveEntry ledger people entry with
                | Ok r -> resolved.Add r
                | Error msg -> diags.Add { Line = line; Column = 1; Message = msg }
        if diags.Count > 0 then
            Error(List.ofSeq diags)
        else
            let entries = List.ofSeq resolved
            let sumFor (pick: ResolvedEntry -> (Person * int64) list) (kind: string) (p: Person) =
                entries
                |> List.filter (fun e -> e.Kind = kind)
                |> List.sumBy (fun e -> pick e |> List.sumBy (fun (q, v) -> if q = p then v else 0L))
            let balances =
                people
                |> List.map (fun p ->
                    let paid = sumFor (fun e -> e.Paid) "expense" p
                    let share = sumFor (fun e -> e.Owed) "expense" p
                    let sent = sumFor (fun e -> e.Paid) "payment" p
                    let received = sumFor (fun e -> e.Owed) "payment" p
                    { Person = p
                      Paid = paid
                      Share = share
                      Sent = sent
                      Received = received
                      Net = paid - share + sent - received })
            Ok
                { Ledger = ledger
                  People = people
                  Entries = entries
                  Balances = balances
                  PairwiseDebts = pairwiseDebts people entries
                  TotalSpend = entries |> List.filter (fun e -> e.Kind = "expense") |> List.sumBy (fun e -> e.BaseAmount) }
