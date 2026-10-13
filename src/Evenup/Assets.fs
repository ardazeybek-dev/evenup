module Evenup.Assets

open System.IO
open System.Reflection

let private assembly = Assembly.GetExecutingAssembly()

let private read (name: string) =
    use stream = assembly.GetManifestResourceStream name
    if isNull stream then None
    else
        use reader = new StreamReader(stream)
        Some(reader.ReadToEnd())

/// The playground page, compiled into the binary so `evenup serve` works from anywhere.
let playground () = read "playground/index.html" |> Option.defaultValue "<h1>playground missing</h1>"

/// Bundled example ledgers: name -> source text.
let examples () =
    assembly.GetManifestResourceNames()
    |> Array.filter (fun n -> n.StartsWith "examples/")
    |> Array.sort
    |> Array.choose (fun n -> read n |> Option.map (fun text -> Path.GetFileNameWithoutExtension n, text))
    |> List.ofArray

let example (name: string) =
    examples () |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
