module Evenup.Program

open System
open System.IO
open System.Text
open System.Text.Json
open Evenup.Terminal

let private usage =
    """evenup — settle shared expenses in the fewest possible transfers

USAGE
  evenup settle <file|->  [--format table|json|md] [--exact-limit N] [--greedy]
  evenup report <file|->  [-o report.html]
  evenup check  <file|->
  evenup serve            [--host H] [--port N]
  evenup example [name]   print a bundled example (no name: list them)
  evenup init   [file]    write a starter ledger (default: trip.evenup)

OPTIONS
  --format       table (default), json or md
  --exact-limit  largest group solved exactly (default 20, max 22)
  --greedy       skip the exact solver
  --no-color     plain output (also honours NO_COLOR)
  -h, --help     this text
  -v, --version  print the version

Settings can also come from a .env file; see .env.example."""

type private Args =
    { Positional: string list
      Flags: Map<string, string> }

let private parseArgs (argv: string list) =
    let valued = set [ "--format"; "--exact-limit"; "-o"; "--output"; "--host"; "--port" ]
    let rec go (acc: Args) =
        function
        | [] -> Ok { acc with Positional = List.rev acc.Positional }
        | flag :: value :: rest when valued.Contains flag -> go { acc with Flags = acc.Flags.Add(flag, value) } rest
        | flag :: [] when valued.Contains flag -> Error(sprintf "%s needs a value" flag)
        | flag :: rest when flag.StartsWith "-" && flag <> "-" -> go { acc with Flags = acc.Flags.Add(flag, "") } rest
        | value :: rest -> go { acc with Positional = value :: acc.Positional } rest
    go { Positional = []; Flags = Map.empty } argv

let private readSource (path: string) =
    if path = "-" then Console.In.ReadToEnd()
    else File.ReadAllText path

let private fail (msg: string) =
    eprintfn "%s %s" (red (bold "error:")) msg
    2

let private withLedger (args: Args) (settings: Config.Settings) (action: string -> Analysis -> Plan -> int) =
    match args.Positional with
    | [ _; path ] when path <> "-" && not (File.Exists path) -> fail (sprintf "no such file: %s" path)
    | [ _; path ] ->
        let source = readSource path
        let limit =
            match args.Flags.TryFind "--exact-limit" |> Option.map Int32.TryParse with
            | Some(true, n) -> n |> max 0 |> min Config.MaxExactLimit
            | _ -> settings.ExactLimit
        match Server.run limit (args.Flags.ContainsKey "--greedy") source with
        | Server.Settled(a, plan) -> action path a plan
        | Server.Invalid ds ->
            printDiagnostics (if path = "-" then "<stdin>" else path) source ds
            1
    | _ -> fail "expected exactly one ledger file (use - for stdin)"

let private settle args settings =
    withLedger args settings (fun _ a plan ->
        match args.Flags.TryFind "--format" |> Option.defaultValue "table" with
        | "json" ->
            let options = JsonSerializerOptions(JsonSerializerDefaults.Web, WriteIndented = true)
            printfn "%s" (JsonSerializer.Serialize(Report.toDto a plan false, options))
            0
        | "md" | "markdown" ->
            printf "%s" (Report.markdown a plan)
            0
        | "table" ->
            printSettlement a plan
            0
        | other -> fail (sprintf "unknown format '%s' (table, json, md)" other))

let private report args settings =
    withLedger args settings (fun path a plan ->
        let out =
            match args.Flags.TryFind "-o" |> Option.orElse (args.Flags.TryFind "--output") with
            | Some o -> o
            | None when path = "-" -> "evenup-report.html"
            | None -> Path.ChangeExtension(path, ".html")
        File.WriteAllText(out, Report.html Config.version a plan, UTF8Encoding false)
        printfn "%s wrote %s %s" (green "✓") (bold out) (dim (sprintf "(%d transfers, %s)" plan.Transfers.Length (Report.methodName plan.Method)))
        0)

let private check args settings =
    withLedger args settings (fun path a _ ->
        printfn "%s %s: %d entries, %d people, balances add up to zero" (green "✓") path a.Entries.Length a.People.Length
        0)

let private example (args: Args) =
    match args.Positional with
    | [ _ ] ->
        printfn "bundled examples:"
        for name, _ in Assets.examples () do
            printfn "  %s" name
        printfn "%s" (dim "print one with: evenup example <name> > my.evenup")
        0
    | [ _; name ] ->
        match Assets.example name with
        | Some text ->
            printf "%s" text
            0
        | None -> fail (sprintf "no example called '%s'" name)
    | _ -> fail "usage: evenup example [name]"

let private init (args: Args) =
    let path = match args.Positional with [ _; p ] -> p | _ -> "trip.evenup"
    if File.Exists path then
        fail (sprintf "%s already exists" path)
    else
        let starter =
            """# evenup ledger — see https://github.com/ardazeybek-dev/evenup/blob/main/docs/format.md

group "Weekend trip"
currency EUR
people alice bob carol

2026-06-01 "Cabin"      alice paid 300
2026-06-01 "Groceries"  bob paid 84.20 for alice bob carol
2026-06-02 "Dinner"     carol paid 96 split shares alice:1 bob:2 carol:1
"""
        File.WriteAllText(path, starter, UTF8Encoding false)
        printfn "%s wrote %s — now run: evenup settle %s" (green "✓") (bold path) path
        0

[<EntryPoint>]
let main argv =
    Console.OutputEncoding <- UTF8Encoding false
    Config.loadDotEnv (Path.Combine(Environment.CurrentDirectory, ".env"))
    let settings = Config.read ()
    match parseArgs (List.ofArray argv) with
    | Error e -> fail e
    | Ok args ->
        if args.Flags.ContainsKey "--no-color" then Terminal.colors <- false
        if args.Flags.ContainsKey "-v" || args.Flags.ContainsKey "--version" then
            printfn "evenup %s" Config.version
            0
        elif args.Flags.ContainsKey "-h" || args.Flags.ContainsKey "--help" || args.Positional.IsEmpty then
            printfn "%s" usage
            0
        else
            try
                match args.Positional.Head with
                | "settle" -> settle args settings
                | "report" -> report args settings
                | "check" -> check args settings
                | "example" | "examples" -> example args
                | "init" -> init args
                | "serve" ->
                    let settings =
                        { settings with
                            Host = args.Flags.TryFind "--host" |> Option.defaultValue settings.Host
                            Port =
                                match args.Flags.TryFind "--port" |> Option.map Int32.TryParse with
                                | Some(true, p) -> p
                                | _ -> settings.Port }
                    Server.start settings
                    0
                | other -> fail (sprintf "unknown command '%s' — try evenup --help" other)
            with :? IOException as e ->
                fail e.Message
