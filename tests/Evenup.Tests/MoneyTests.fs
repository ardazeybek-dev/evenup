module Evenup.Tests.MoneyTests

open System.Globalization
open System.Numerics
open System.Threading
open Xunit
open FsCheck.Xunit
open Evenup

[<Fact>]
let ``parses amounts into minor units`` () =
    Assert.Equal(Ok 84000L, Money.tryParseMinor 2 "840.00")
    Assert.Equal(Ok 12640L, Money.tryParseMinor 2 "126.4")
    Assert.Equal(Ok -350L, Money.tryParseMinor 2 "-3.50")
    Assert.Equal(Ok 120050L, Money.tryParseMinor 2 "1_200.50")
    Assert.Equal(Ok 1500L, Money.tryParseMinor 0 "1500")

[<Fact>]
let ``rejects more precision than the currency has`` () =
    Assert.True(Result.isError (Money.tryParseMinor 2 "12.345"))
    Assert.True(Result.isError (Money.tryParseMinor 0 "100.5"))
    Assert.True(Result.isError (Money.tryParseMinor 2 "12,50"))

[<Fact>]
let ``knows currency exponents`` () =
    Assert.Equal(2, Money.exponent "EUR")
    Assert.Equal(0, Money.exponent "JPY")
    Assert.Equal(3, Money.exponent "KWD")

[<Fact>]
let ``formats with symbols and grouping`` () =
    Assert.Equal("€1,234.56", Money.format "EUR" 123456L)
    Assert.Equal("-$0.05", Money.format "USD" -5L)
    Assert.Equal("¥1,500", Money.format "JPY" 1500L)
    Assert.Equal("12.50 CHF", Money.format "CHF" 1250L)

[<Fact>]
let ``parsing and formatting ignore the machine culture`` () =
    // tr-TR uses ',' as the decimal separator and has the dotted/dotless i
    let previous = Thread.CurrentThread.CurrentCulture
    try
        Thread.CurrentThread.CurrentCulture <- CultureInfo "tr-TR"
        Assert.Equal(Ok 84000L, Money.tryParseMinor 2 "840.00")
        Assert.Equal("€1,234.56", Money.format "EUR" 123456L)
        Assert.True(Money.isCode "TRY")
        Assert.False(Money.isCode "try")
    finally
        Thread.CurrentThread.CurrentCulture <- previous

[<Fact>]
let ``converts with banker's rounding`` () =
    // 10.00 USD * 0.92 = 9.20 EUR
    Assert.Equal(920L, Money.convert "USD" "EUR" 0.92m 1000L)
    // 1 JPY = 0.0061 EUR; 1500 JPY = 9.15 EUR
    Assert.Equal(915L, Money.convert "JPY" "EUR" 0.0061m 1500L)
    // 0.125 → 0.12 (round half to even)
    Assert.Equal(12L, Money.convert "USD" "EUR" 0.5m 25L)

[<Fact>]
let ``allocate splits 100 cents three ways without losing a cent`` () =
    let parts = Money.allocateBy 100L [| 1L; 1L; 1L |]
    Assert.Equal<int64[]>([| 34L; 33L; 33L |], parts)

[<Fact>]
let ``allocate gives remainders to the largest fractions first`` () =
    // ideal: 2.857, 4.285, 2.857 -> 3, 4, 3
    let parts = Money.allocateBy 10L [| 2L; 3L; 2L |]
    Assert.Equal<int64[]>([| 3L; 4L; 3L |], parts)

[<Fact>]
let ``allocate keeps the sign of negative totals`` () =
    let parts = Money.allocateBy -100L [| 1L; 1L; 1L |]
    Assert.Equal<int64[]>([| -34L; -33L; -33L |], parts)

[<Property>]
let ``allocate always sums to the total and stays within one unit of ideal`` (total: int, weights: byte list) =
    let ws = weights |> List.truncate 12 |> List.map int64
    if List.isEmpty ws || List.sum ws = 0L then
        true
    else
        let t = int64 total
        let parts = Money.allocateBy t (Array.ofList ws)
        let sumW = List.sum ws
        let sumOk = Array.sum parts = t
        let closeOk =
            Array.forall2
                (fun (p: int64) (w: int64) ->
                    let ideal = BigInteger t * BigInteger w
                    let diff = BigInteger p * BigInteger sumW - ideal
                    BigInteger.Abs diff < BigInteger sumW)
                parts
                (Array.ofList ws)
        sumOk && closeOk
