open System
open System.IO
open System.Text.Json.Nodes
open Paket
open Paket.Domain

let nugetPath = @"D:\temp\saturnblabla\nugety\packages.lock.json"
let paketPath = @"D:\temp\saturnblabla\paket.lock"

let printNuGet () =
    let nugetText = File.ReadAllText nugetPath
    let nugetLock = JsonNode.Parse nugetText
    let deps = nugetLock.["dependencies"].AsObject()
    let netcore3 = deps.[".NETCoreApp,Version=v3.0"].AsObject()
    netcore3
    |> Seq.map (fun kv ->
        let prop = kv.Value.AsObject()
        let v = prop.["resolved"].GetValue<string>()
        let v = if v.EndsWith (".0") then v.Substring(0,v.Length-2) else v
        kv.Key,v)
    |> Seq.sortBy fst
    |> Seq.iter (fun (n,v) -> printfn "%s, %s" n v)


let printPaket() =
    let paketLock = LockFile.LoadFrom paketPath
    paketLock.Groups.[GroupName "Main"].Resolution
    |> Seq.map (fun r -> r.Key.Name,r.Value.Version.ToString())
    |> Seq.sortBy fst
    |> Seq.iter (fun (n,v) -> printfn "%s, %s" n v)

[<EntryPoint>]
let main argv =
    printNuGet ()
    0
