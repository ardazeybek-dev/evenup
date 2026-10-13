module Evenup.Tests.ReportTests

open System.Text.Json
open Xunit
open Evenup
open Evenup.Tests.EngineTests

let private sample =
    "group \"<script>alert(1)</script> & co\"\ncurrency EUR\npeople a b c\n2026-05-01 a paid 90\n2026-05-02 b paid 30 for c\n"

[<Fact>]
let ``html report is self-contained and escapes user text`` () =
    let a = analyze sample
    let html = Report.html "test" a (Settle.plan 20 (Settle.balancesOf a))
    Assert.StartsWith("<!doctype html>", html)
    Assert.Contains("&lt;script&gt;", html)
    Assert.DoesNotContain("<script>alert", html)
    Assert.Contains("<svg", html)
    Assert.DoesNotContain("http://", html.Replace("http://www.w3.org/2000/svg", ""))

[<Fact>]
let ``json dto round-trips through System.Text.Json`` () =
    let a = analyze sample
    let dto = Report.toDto a (Settle.plan 20 (Settle.balancesOf a)) false
    let json = JsonSerializer.Serialize(dto, JsonSerializerOptions(JsonSerializerDefaults.Web))
    use doc = JsonDocument.Parse json
    let root = doc.RootElement
    Assert.Equal("EUR", root.GetProperty("currency").GetString())
    Assert.Equal(3, root.GetProperty("people").GetArrayLength())
    Assert.Equal("optimal", root.GetProperty("plan").GetProperty("method").GetString())
    let first = root.GetProperty("transfers").EnumerateArray() |> Seq.head
    Assert.Equal("a", first.GetProperty("to").GetString())

[<Fact>]
let ``markdown lists every transfer`` () =
    let a = analyze sample
    let plan = Settle.plan 20 (Settle.balancesOf a)
    let md = Report.markdown a plan
    for t in plan.Transfers do
        Assert.Contains(sprintf "**%s** pays **%s**" t.From t.To, md)
