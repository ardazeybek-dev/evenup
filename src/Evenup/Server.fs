module Evenup.Server

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging

let private json = JsonSerializerOptions(JsonSerializerDefaults.Web)

type Outcome =
    | Settled of Analysis * Plan
    | Invalid of Diagnostic list

/// Parse → analyse → plan. Shared by the API and the CLI.
let run (exactLimit: int) (greedyOnly: bool) (source: string) =
    match Parser.parse source |> Result.bind Engine.analyze with
    | Error ds -> Invalid ds
    | Ok a ->
        let balances = Settle.balancesOf a
        let plan =
            if greedyOnly then
                { Transfers = Settle.greedy balances
                  Method = Greedy
                  Groups = []
                  Participants = balances |> List.filter (fun (_, v) -> v <> 0L) |> List.length }
            else
                Settle.plan exactLimit balances
        Settled(a, plan)

let private readBody (ctx: HttpContext) =
    task {
        use reader = new StreamReader(ctx.Request.Body)
        return! reader.ReadToEndAsync()
    }

let private errorsDto (ds: Diagnostic list) =
    {| errors = ds |> List.map (fun d -> {| line = d.Line; column = d.Column; message = d.Message |}) |}

/// Reads the ledger from the body; an oversized body becomes a clean 413 instead of a logged crash.
let private withSource (ctx: HttpContext) (handle: string -> IResult) : Task<IResult> =
    task {
        try
            let! source = readBody ctx
            return handle source
        with :? BadHttpRequestException as e ->
            let diag = { Line = 0; Column = 0; Message = e.Message }
            return Results.Json(errorsDto [ diag ], json, statusCode = e.StatusCode)
    }

let private limitFrom (ctx: HttpContext) (fallback: int) =
    match ctx.Request.Query.TryGetValue "exactLimit" with
    | true, v ->
        match Int32.TryParse(string v) with
        | true, n -> n |> max 0 |> min Config.MaxExactLimit
        | _ -> fallback
    | _ -> fallback

let start (settings: Config.Settings) =
    let options = WebApplicationOptions(ContentRootPath = AppContext.BaseDirectory)
    let builder = WebApplication.CreateBuilder(options)
    builder.Logging.ClearProviders().AddSimpleConsole(fun o -> o.SingleLine <- true) |> ignore
    match Enum.TryParse<LogLevel>(settings.LogLevel, true) with
    | true, level -> builder.Logging.SetMinimumLevel level |> ignore
    | _ -> ()
    builder.WebHost.ConfigureKestrel(fun k -> k.Limits.MaxRequestBodySize <- Nullable(int64 settings.MaxBodyKb * 1024L)) |> ignore
    if not settings.CorsOrigins.IsEmpty then
        builder.Services.AddCors(fun o ->
            o.AddDefaultPolicy(fun p ->
                p.WithOrigins(Array.ofList settings.CorsOrigins).AllowAnyHeader().WithMethods("GET", "POST") |> ignore))
        |> ignore
    let app = builder.Build()
    if not settings.CorsOrigins.IsEmpty then app.UseCors() |> ignore

    let page = Assets.playground().Replace("/*THEME*/", Report.themeCss)
    app.MapGet("/", Func<IResult>(fun () -> Results.Content(page, "text/html; charset=utf-8"))) |> ignore

    app.MapGet("/api/health", Func<IResult>(fun () ->
        Results.Json({| status = "ok"; version = Config.version; exactLimit = settings.ExactLimit |}, json)))
    |> ignore

    app.MapGet("/api/examples", Func<IResult>(fun () ->
        Results.Json(Assets.examples () |> List.map fst, json)))
    |> ignore

    app.MapGet("/api/examples/{name}", Func<string, IResult>(fun name ->
        match Assets.example name with
        | Some text -> Results.Text(text, "text/plain; charset=utf-8")
        | None -> Results.NotFound()))
    |> ignore

    app.MapPost("/api/settle", Func<HttpContext, Task<IResult>>(fun ctx ->
        withSource ctx (fun source ->
            let greedy = ctx.Request.Query.ContainsKey "greedy"
            match run (limitFrom ctx settings.ExactLimit) greedy source with
            | Settled(a, plan) -> Results.Json(Report.toDto a plan true, json)
            | Invalid ds -> Results.Json(errorsDto ds, json, statusCode = 422))))
    |> ignore

    app.MapPost("/api/report", Func<HttpContext, Task<IResult>>(fun ctx ->
        withSource ctx (fun source ->
            match run (limitFrom ctx settings.ExactLimit) false source with
            | Settled(a, plan) -> Results.Content(Report.html Config.version a plan, "text/html; charset=utf-8")
            | Invalid ds -> Results.Json(errorsDto ds, json, statusCode = 422))))
    |> ignore

    let url = sprintf "http://%s:%d" settings.Host settings.Port
    printfn "%s evenup %s listening on %s" (Terminal.green "●") Config.version (Terminal.bold url)
    printfn "%s" (Terminal.dim "  playground at /, API at /api/settle — Ctrl+C to stop")
    app.Run url
