namespace Evenup

open System
open System.Globalization

/// Parser for the plain-text `.evenup` ledger format. See docs/format.md.
///
///     group "Lisbon 2026"
///     currency EUR
///     rate USD 0.92
///     people alice bob carol
///
///     2026-05-01 "Airbnb" alice paid 840
///     2026-05-02 "Dinner" bob paid 126.40 split shares alice:2 bob:1 carol:1
///     2026-05-03 carol paid alice 20
module Parser =

    type private Token =
        { Text: string
          Col: int
          Quoted: bool }

    exception private ParseError of col: int * message: string

    let private fail col msg = raise (ParseError(col, msg))

    let keywords =
        set [ "group"; "currency"; "rate"; "people"; "paid"; "split"; "equally"; "among"; "for"
              "shares"; "percent"; "exact"; "itemized" ]

    let private isKw (k: string) (t: Token) =
        not t.Quoted && String.Equals(t.Text, k, StringComparison.OrdinalIgnoreCase)

    let private isKeyword (s: string) =
        keywords |> Set.exists (fun k -> String.Equals(k, s, StringComparison.OrdinalIgnoreCase))

    /// Names start with a letter (any script — "ayşe" and "zoë" are fine) and continue
    /// with letters, digits, '_' or '-'.
    let isValidName (s: string) =
        s.Length > 0
        && Char.IsLetter s[0]
        && s |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '-')
        && not (isKeyword s)

    let private isNumberLike (s: string) =
        s.Length > 0
        && (Char.IsDigit s[0]
            || ((s[0] = '-' || s[0] = '.') && s.Length > 1 && (Char.IsDigit s[1] || s[1] = '.')))

    let private tryDate (s: string) =
        match DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, d -> Some d
        | _ -> None

    /// Splits a line into tokens. Quotes group words, '#' starts a comment,
    /// commas separate like spaces, and "name : 20" is glued into "name:20".
    let private tokenize (line: string) : Token list =
        let raw = ResizeArray<Token>()
        let mutable i = 0
        let n = line.Length
        let mutable stop = false
        while not stop && i < n do
            let c = line[i]
            if Char.IsWhiteSpace c || c = ',' then
                i <- i + 1
            elif c = '#' then
                stop <- true
            elif c = '"' then
                let start = i
                let close = line.IndexOf('"', i + 1)
                if close < 0 then fail (start + 1) "unterminated string"
                raw.Add { Text = line.Substring(i + 1, close - i - 1); Col = start + 1; Quoted = true }
                i <- close + 1
            else
                let start = i
                while i < n && not (Char.IsWhiteSpace line[i]) && line[i] <> ',' do
                    i <- i + 1
                raw.Add { Text = line.Substring(start, i - start); Col = start + 1; Quoted = false }
        // glue "alice :", "alice: 20", "alice : 20" into "alice:20"
        let out = ResizeArray<Token>()
        let mutable k = 0
        while k < raw.Count do
            let t = raw[k]
            if not t.Quoted && t.Text = ":" && out.Count > 0 && k + 1 < raw.Count then
                let prev = out[out.Count - 1]
                out[out.Count - 1] <- { prev with Text = prev.Text + ":" + raw[k + 1].Text }
                k <- k + 2
            elif not t.Quoted && t.Text.EndsWith ":" && t.Text.Length > 1 && k + 1 < raw.Count then
                out.Add { t with Text = t.Text + raw[k + 1].Text }
                k <- k + 2
            else
                out.Add t
                k <- k + 1
        List.ofSeq out

    let private expectName (t: Token) =
        if t.Quoted || not (isValidName t.Text) then
            fail t.Col (sprintf "expected a person's name, found '%s'" t.Text)
        t.Text

    let private amount (currency: string) (t: Token) =
        match Money.tryParseMinor (Money.exponent currency) t.Text with
        | Ok v -> v
        | Error e -> fail t.Col e

    let private decimalOf (t: Token) (text: string) =
        match Money.tryParseDecimal text with
        | Ok v -> v
        | Error e -> fail t.Col e

    /// "alice:20" -> ("alice", "20")
    let private pair (t: Token) =
        let idx = t.Text.LastIndexOf ':'
        if t.Quoted || idx <= 0 || idx = t.Text.Length - 1 then
            fail t.Col (sprintf "expected name:value, found '%s'" t.Text)
        let name = t.Text.Substring(0, idx)
        if not (isValidName name) then fail t.Col (sprintf "'%s' is not a valid name" name)
        name, t.Text.Substring(idx + 1)

    let private nonEmpty (what: string) (col: int) (xs: 'a list) =
        if List.isEmpty xs then fail col (sprintf "expected at least one %s" what)
        xs

    type private Pending =
        { Header: Expense
          Items: Item list }

    type private State =
        { mutable Name: string
          mutable Base: string option
          mutable Rates: Map<string, decimal>
          mutable People: Person list
          Entries: ResizeArray<Entry>
          mutable Pending: Pending option
          mutable PendingLine: int }

    let private parseSplit (currency: string) (col: int) (rest: Token list) : Split * bool =
        // returns the split and whether item lines are expected to follow
        let names ts = ts |> List.map expectName
        match rest with
        | [] -> Equal [], false
        | f :: ts when isKw "for" f -> Equal(names ts |> nonEmpty "name after 'for'" f.Col), false
        | [ i ] when isKw "itemized" i -> Itemized [], true
        | s :: ts when isKw "split" s ->
            match ts with
            | [] -> fail s.Col "expected equally, among, shares, percent, exact or itemized after 'split'"
            | [ e ] when isKw "equally" e -> Equal [], false
            | e :: a :: ns when isKw "equally" e && isKw "among" a -> Equal(names ns |> nonEmpty "name after 'among'" a.Col), false
            | a :: ns when isKw "among" a -> Equal(names ns |> nonEmpty "name after 'among'" a.Col), false
            | k :: ps when isKw "shares" k ->
                let xs = ps |> List.map (fun p -> let n, v = pair p in n, decimalOf p v)
                Shares(xs |> nonEmpty "name:weight pair" k.Col), false
            | k :: ps when isKw "percent" k ->
                let xs = ps |> List.map (fun p -> let n, v = pair p in n, decimalOf p (v.TrimEnd '%'))
                Percent(xs |> nonEmpty "name:percent pair" k.Col), false
            | k :: ps when isKw "exact" k ->
                let xs =
                    ps
                    |> List.map (fun p ->
                        let n, v = pair p
                        n, amount currency { p with Text = v })
                Exact(xs |> nonEmpty "name:amount pair" k.Col), false
            | [ k ] when isKw "itemized" k -> Itemized [], true
            | k :: _ -> fail k.Col (sprintf "unknown split '%s'" k.Text)
        | t :: _ -> fail t.Col (sprintf "unexpected '%s' (expected 'split', 'for' or end of line)" t.Text)

    let private parseEntry (st: State) (lineNo: int) (tokens: Token list) =
        let date, tokens =
            match tokens with
            | t :: rest when not t.Quoted && (tryDate t.Text).IsSome -> tryDate t.Text, rest
            | t :: _ when not t.Quoted && t.Text.Length = 10 && Char.IsDigit t.Text[0] ->
                fail t.Col (sprintf "'%s' is not a valid yyyy-MM-dd date" t.Text)
            | _ -> None, tokens
        let title, tokens =
            match tokens with
            | t :: rest when t.Quoted -> Some t.Text, rest
            | _ -> None, tokens
        let payerToks = tokens |> List.takeWhile (fun t -> not (isKw "paid" t))
        let after = tokens |> List.skipWhile (fun t -> not (isKw "paid" t))
        match after with
        | [] ->
            let col = match tokens with t :: _ -> t.Col | [] -> 1
            fail col "expected '<name> paid <amount>' or a directive (group, currency, rate, people)"
        | paid :: rest ->
            if List.isEmpty payerToks then fail paid.Col "who paid? expected a name before 'paid'"
            let baseCode =
                match st.Base with
                | Some b -> b
                | None ->
                    st.Base <- Some "USD"
                    "USD"
            let currencyAndRest (ts: Token list) =
                match ts with
                | c :: more when not c.Quoted && Money.isCode c.Text -> c.Text, more
                | _ -> baseCode, ts
            match rest with
            | [] -> fail (paid.Col + 4) "expected an amount after 'paid'"
            | toTok :: more when not (isNumberLike toTok.Text) ->
                // "alice paid bob 50 [EUR]" — a direct payment
                let from =
                    match payerToks with
                    | [ p ] -> expectName p
                    | p :: _ -> fail p.Col "a payment has exactly one sender"
                    | [] -> fail paid.Col "missing sender"
                let target = expectName toTok
                match more with
                | [] -> fail toTok.Col "expected an amount after the recipient"
                | amtTok :: tail ->
                    let currency, tail = currencyAndRest tail
                    if not (List.isEmpty tail) then fail tail.Head.Col (sprintf "unexpected '%s'" tail.Head.Text)
                    if from = target then fail toTok.Col "someone cannot pay themselves"
                    let value = amount currency amtTok
                    if value <= 0L then fail amtTok.Col "a payment must be positive"
                    let p: Payment =
                        { Line = lineNo
                          Date = date
                          From = from
                          To = target
                          Amount = value
                          Currency = currency }
                    st.Entries.Add(PaymentEntry p)
            | amtTok :: tail ->
                let currency, tail = currencyAndRest tail
                let total = amount currency amtTok
                if total = 0L then fail amtTok.Col "amount cannot be zero"
                let payers =
                    match payerToks with
                    | [ p ] when not (p.Text.Contains ':') -> One(expectName p)
                    | ps ->
                        Many(ps |> List.map (fun p -> let n, v = pair p in n, amount currency { p with Text = v }))
                let split, wantsItems = parseSplit currency paid.Col tail
                let e: Expense =
                    { Line = lineNo
                      Date = date
                      Title = defaultArg title ""
                      Payers = payers
                      Amount = total
                      Currency = currency
                      Split = split }
                if wantsItems then
                    st.Pending <- Some { Header = e; Items = [] }
                    st.PendingLine <- lineNo
                else
                    st.Entries.Add(ExpenseEntry e)

    let private parseItem (st: State) (pending: Pending) (tokens: Token list) =
        let label, tokens =
            match tokens with
            | t :: rest when t.Quoted -> t.Text, rest
            | _ -> "", tokens
        match List.rev tokens with
        | amt :: revNames ->
            let names = revNames |> List.rev |> List.map expectName |> nonEmpty "name on the item line" amt.Col
            let value = amount pending.Header.Currency amt
            if value < 0L then fail amt.Col "item amounts cannot be negative"
            let item = { Label = label; People = names; Amount = value }
            st.Pending <- Some { pending with Items = pending.Items @ [ item ] }
        | [] -> fail 1 "expected '<names> <amount>' on the item line"

    let private flush (st: State) (diags: ResizeArray<Diagnostic>) =
        match st.Pending with
        | Some p ->
            if List.isEmpty p.Items then
                diags.Add { Line = st.PendingLine; Column = 1; Message = "itemized expense has no indented item lines" }
            else
                st.Entries.Add(ExpenseEntry { p.Header with Split = Itemized p.Items })
            st.Pending <- None
        | None -> ()

    let private parseDirective (st: State) (tokens: Token list) =
        match tokens with
        | g :: rest when isKw "group" g ->
            if List.isEmpty rest then fail g.Col "expected a group name"
            st.Name <- rest |> List.map (fun t -> t.Text) |> String.concat " "
            true
        | c :: rest when isKw "currency" c ->
            match rest with
            | [ code ] when Money.isCode code.Text ->
                if st.Entries.Count > 0 || st.Pending.IsSome then
                    fail c.Col "'currency' must come before the first expense"
                st.Base <- Some code.Text
            | _ -> fail c.Col "expected a three-letter currency code, e.g. 'currency EUR'"
            true
        | r :: rest when isKw "rate" r ->
            match rest with
            | [ code; value ] when Money.isCode code.Text ->
                let v = decimalOf value value.Text
                if v <= 0m then fail value.Col "a rate must be positive"
                st.Rates <- st.Rates.Add(code.Text, v)
            | _ -> fail r.Col "expected 'rate <CODE> <value>', e.g. 'rate USD 0.92'"
            true
        | p :: rest when isKw "people" p ->
            let names = rest |> List.map expectName |> nonEmpty "name" p.Col
            match names |> List.countBy id |> List.tryFind (fun (_, n) -> n > 1) with
            | Some(dup, _) -> fail p.Col (sprintf "'%s' is listed twice" dup)
            | None -> st.People <- st.People @ names
            true
        | _ -> false

    /// Parses ledger text. Collects every line error instead of stopping at the first.
    let parse (text: string) : Result<Ledger, Diagnostic list> =
        let st =
            { Name = "Untitled group"
              Base = None
              Rates = Map.empty
              People = []
              Entries = ResizeArray()
              Pending = None
              PendingLine = 0 }
        let diags = ResizeArray<Diagnostic>()
        let lines = text.Replace("\r\n", "\n").Split '\n'
        lines
        |> Array.iteri (fun idx line ->
            let lineNo = idx + 1
            try
                let tokens = tokenize line
                if not (List.isEmpty tokens) then
                    let indented = line.Length > 0 && Char.IsWhiteSpace line[0]
                    match st.Pending with
                    | Some p when indented -> parseItem st p tokens
                    | _ ->
                        flush st diags
                        if indented then fail 1 "unexpected indentation (only item lines of an 'itemized' expense are indented)"
                        if not (parseDirective st tokens) then parseEntry st lineNo tokens
            with ParseError(col, msg) ->
                diags.Add { Line = lineNo; Column = col; Message = msg })
        flush st diags
        if diags.Count > 0 then
            Error(List.ofSeq diags)
        else
            Ok
                { Name = st.Name
                  Base = defaultArg st.Base "USD"
                  Rates = st.Rates
                  People = st.People
                  Entries = List.ofSeq st.Entries }
