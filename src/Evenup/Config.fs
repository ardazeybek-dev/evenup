module Evenup.Config

open System
open System.IO

/// Loads `KEY=value` pairs from a .env file into the process environment.
/// Variables that are already set win, so real environment always overrides the file.
let loadDotEnv (path: string) =
    if File.Exists path then
        for raw in File.ReadAllLines path do
            let line = raw.Trim()
            if line <> "" && not (line.StartsWith "#") then
                let line = if line.StartsWith "export " then line.Substring 7 else line
                match line.IndexOf '=' with
                | idx when idx > 0 ->
                    let key = line.Substring(0, idx).Trim()
                    let value = line.Substring(idx + 1).Trim()
                    let value =
                        if value.Length >= 2 && ((value.StartsWith "\"" && value.EndsWith "\"") || (value.StartsWith "'" && value.EndsWith "'"))
                        then value.Substring(1, value.Length - 2)
                        else
                            // strip trailing " # comment" on unquoted values
                            match value.IndexOf " #" with
                            | -1 -> value
                            | i -> value.Substring(0, i).TrimEnd()
                    if String.IsNullOrEmpty(Environment.GetEnvironmentVariable key) then
                        Environment.SetEnvironmentVariable(key, value)
                | _ -> ()

let private env (key: string) =
    match Environment.GetEnvironmentVariable key with
    | null | "" -> None
    | v -> Some v

let private intEnv key fallback =
    match env key |> Option.map Int32.TryParse with
    | Some(true, v) -> v
    | _ -> fallback

/// Upper bound for the exact solver: 2^22 states ≈ 40 MB, beyond that memory explodes.
[<Literal>]
let MaxExactLimit = 22

type Settings =
    { Host: string
      Port: int
      ExactLimit: int
      MaxBodyKb: int
      CorsOrigins: string list
      LogLevel: string }

let read () =
    { Host = env "EVENUP_HOST" |> Option.defaultValue "127.0.0.1"
      Port = intEnv "PORT" (intEnv "EVENUP_PORT" 5317)
      ExactLimit = intEnv "EVENUP_EXACT_LIMIT" Settle.DefaultExactLimit |> max 0 |> min MaxExactLimit
      MaxBodyKb = intEnv "EVENUP_MAX_BODY_KB" 256 |> max 1
      CorsOrigins =
        env "EVENUP_CORS_ORIGINS"
        |> Option.map (fun s -> s.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries) |> List.ofArray)
        |> Option.defaultValue []
      LogLevel = env "EVENUP_LOG_LEVEL" |> Option.defaultValue "Warning" }

let version =
    let attr =
        Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof<Reflection.AssemblyInformationalVersionAttribute>, false)
        |> Array.tryHead
        |> Option.map (fun a -> (a :?> Reflection.AssemblyInformationalVersionAttribute).InformationalVersion)
    match attr with
    | Some v -> v.Split('+').[0]
    | None -> "0.0.0"
