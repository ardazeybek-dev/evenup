namespace Evenup

open System
open System.Globalization
open System.Numerics

/// Amounts are always whole numbers of a currency's minor unit (cents, pence, yen).
/// Floating point never touches money in this code base.
module Money =

    let private inv = CultureInfo.InvariantCulture

    /// ISO 4217 minor-unit exponents that differ from the default of 2.
    let private exponents =
        dict [
            "JPY", 0; "KRW", 0; "VND", 0; "CLP", 0; "ISK", 0; "UGX", 0; "PYG", 0
            "BHD", 3; "KWD", 3; "OMR", 3; "JOD", 3; "TND", 3; "IQD", 3; "LYD", 3
        ]

    let private symbols =
        dict [ "EUR", "€"; "USD", "$"; "GBP", "£"; "JPY", "¥"; "TRY", "₺"; "INR", "₹"; "KRW", "₩" ]

    /// Number of decimal places the currency uses.
    let exponent (code: string) =
        match exponents.TryGetValue code with
        | true, e -> e
        | _ -> 2

    let pow10 (n: int) = pown 10L n

    /// A currency code is three ASCII upper-case letters. Checked without ToUpper
    /// so the Turkish dotted/dotless i can never sneak in.
    let isCode (s: string) =
        s.Length = 3 && s |> Seq.forall (fun c -> c >= 'A' && c <= 'Z')

    /// Parses "1234.56", "-3", "1_200.50" into minor units, rejecting extra precision.
    /// Always culture-invariant: "12.50" means twelve and a half on every machine.
    let tryParseMinor (exp: int) (text: string) : Result<int64, string> =
        let t = text.Replace("_", "")
        let styles = NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint
        match Decimal.TryParse(t, styles, inv) with
        | true, d ->
            let scaled = d * decimal (pow10 exp)
            if scaled <> Decimal.Truncate scaled then
                Error(sprintf "'%s' has more than %d decimal place(s)" text exp)
            elif abs scaled > 900_000_000_000_000m then
                Error(sprintf "'%s' is too large" text)
            else
                Ok(int64 scaled)
        | _ -> Error(sprintf "'%s' is not a number" text)

    /// Parses a plain decimal (weights, percentages, exchange rates).
    let tryParseDecimal (text: string) : Result<decimal, string> =
        let styles = NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint
        match Decimal.TryParse(text.Replace("_", ""), styles, inv) with
        | true, d -> Ok d
        | _ -> Error(sprintf "'%s' is not a number" text)

    /// "1,234.56" — grouping with commas, always invariant.
    let formatPlain (exp: int) (minor: int64) =
        (decimal minor / decimal (pow10 exp)).ToString("N" + string exp, inv)

    /// "€1,234.56" for well-known currencies, "1,234.56 CHF" otherwise.
    let format (code: string) (minor: int64) =
        let body = formatPlain (exponent code) (abs minor)
        let sign = if minor < 0L then "-" else ""
        match symbols.TryGetValue code with
        | true, s -> sign + s + body
        | _ -> sprintf "%s%s %s" sign body code

    /// Converts minor units of one currency into another using `rate`
    /// (1 unit of `fromCode` = `rate` units of `toCode`). Banker's rounding.
    let convert (fromCode: string) (toCode: string) (rate: decimal) (minor: int64) =
        let major = decimal minor / decimal (pow10 (exponent fromCode))
        let target = major * rate * decimal (pow10 (exponent toCode))
        int64 (Math.Round(target, MidpointRounding.ToEven))

    /// Turns a decimal weight such as 1.5 or 33.33 into an exact integer weight.
    let weightOf (d: decimal) : Result<BigInteger, string> =
        let scaled = d * 1_000_000m
        if d < 0m then Error "weights cannot be negative"
        elif scaled <> Decimal.Truncate scaled then Error(sprintf "weight %M has too many decimals" d)
        else Ok(BigInteger scaled)

    /// Splits `total` across `weights` proportionally using the largest-remainder
    /// (Hamilton) method. The parts always add up to exactly `total`, every part is
    /// within one minor unit of its ideal value, and ties go to the earlier index,
    /// so the result is deterministic.
    let allocate (total: int64) (weights: BigInteger[]) : int64[] =
        if weights.Length = 0 then invalidArg (nameof weights) "nothing to allocate to"
        if weights |> Array.exists (fun w -> w.Sign < 0) then invalidArg (nameof weights) "negative weight"
        let sum = weights |> Array.fold (+) BigInteger.Zero
        if sum.IsZero then invalidArg (nameof weights) "weights add up to zero"
        let t = BigInteger(abs total)
        let floors = weights |> Array.map (fun w -> t * w / sum)
        let remainders = weights |> Array.map (fun w -> t * w % sum)
        let leftover = int (t - (floors |> Array.fold (+) BigInteger.Zero))
        let result = floors |> Array.map int64
        remainders
        |> Array.mapi (fun i r -> i, r)
        |> Array.sortWith (fun (i1, r1) (i2, r2) ->
            match compare r2 r1 with
            | 0 -> compare i1 i2
            | c -> c)
        |> Array.truncate leftover
        |> Array.iter (fun (i, _) -> result[i] <- result[i] + 1L)
        if total < 0L then result |> Array.map (~-) else result

    /// Integer weights convenience wrapper.
    let allocateBy (total: int64) (weights: int64[]) =
        allocate total (weights |> Array.map (fun w -> BigInteger(abs w)))
