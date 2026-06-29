/// One-shot migration of an existing Paket setup to Faket for straightforward repositories.
/// Dry-run by default; only writes when `apply` is true. Bails loudly (ManualReview) on the
/// cases that need a human rather than silently half-migrating.
module Paket.MigrateProcess

open System.IO
open System.Text.Json.Nodes
open Paket.Logging

type MigrationAction =
    /// Rewrite the `paket` tool entry in .config/dotnet-tools.json to `faket`.
    | UpdateToolManifest of path:string
    /// Re-extract the (faket-aware) .paket/Paket.Restore.targets.
    | RefreshRestoreTargets of root:string
    /// Delete a legacy file (e.g. the bootstrapper exe).
    | RemoveLegacyFile of path:string
    /// Something that needs a human; never auto-applied.
    | ManualReview of description:string

let private skipDir (d:string) =
    let n = Path.GetFileName(d.TrimEnd('/','\\'))
    n = "bin" || n = "obj" || n = ".git" || n = "packages" || n = "paket-files" || n = "node_modules"

let rec private walk (dir:string) : string seq =
    seq {
        let entries = try Directory.GetFileSystemEntries dir with _ -> [||]
        for e in entries do
            if Directory.Exists e then
                if not (skipDir e) then yield! walk e
            else yield e }

/// Builds the migration plan for a repository root. Empty list => nothing to do / not a Paket repo.
let plan (root:string) : MigrationAction list =
    [ let depsFile = Path.Combine(root, "paket.dependencies")
      if File.Exists depsFile then
          // tooling manifest
          let manifest = Path.Combine(root, ".config", "dotnet-tools.json")
          if File.Exists manifest && (File.ReadAllText manifest).Contains "\"paket\"" then
              yield UpdateToolManifest manifest

          // MSBuild restore targets
          if File.Exists (Path.Combine(root, ".paket", "Paket.Restore.targets")) then
              yield RefreshRestoreTargets root

          // legacy bootstrapper / vendored exe
          for name in [ "paket.bootstrapper.exe"; "paket.exe"; "paket.bootstrapper.dll" ] do
              let p = Path.Combine(root, ".paket", name)
              if File.Exists p then yield RemoveLegacyFile p

          // cases we refuse to touch automatically
          if File.Exists (Path.Combine(root, "paket.local")) then
              yield ManualReview "paket.local present - local source overrides are not migrated automatically"
          let magicConfig = Path.Combine(root, ".paket", "paket.bootstrapper.config")
          if File.Exists magicConfig then
              yield ManualReview (sprintf "%s present - custom bootstrapper configuration must be migrated by hand" magicConfig) ]

let private rewriteToolManifest (faketVersion:string) (path:string) =
    let node = JsonNode.Parse(File.ReadAllText path)
    let tools = node.["tools"].AsObject()
    match tools.["paket"] with
    | null -> false
    | paketNode ->
        let faket = JsonObject()
        // Pin to the running faket's version, not the old paket version.
        faket.["version"] <- JsonValue.Create(faketVersion)
        let cmds = JsonArray()
        cmds.Add(JsonValue.Create("faket"))
        faket.["commands"] <- cmds
        match paketNode.["rollForward"] with
        | null -> ()
        | rf -> faket.["rollForward"] <- JsonValue.Create(rf.GetValue<bool>())
        tools.Remove("paket") |> ignore
        tools.["faket"] <- faket
        let opts = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
        File.WriteAllText(path, node.ToJsonString(opts))
        true

/// Invalidates the restore cache so the next build/restore regenerates the per-project
/// obj props against the refreshed (faket) targets. Without this, projects relying on
/// paket-delivered type providers/generators can fail to build until a forced restore.
let private invalidateRestoreCache (root:string) =
    let hashFile = Path.Combine(root, Constants.PaketRestoreHashFilePath)
    if File.Exists hashFile then try File.Delete hashFile with _ -> ()

/// Runs the plan. When apply=false, only reports. `faketVersion` is the version the tool
/// manifest will be pinned to (the running faket's version). Returns the actions taken.
let run (root:string) (faketVersion:string) (apply:bool) : MigrationAction list =
    let actions = plan root
    if List.isEmpty actions then
        tracefn "No Paket setup found at %s (looked for paket.dependencies)." root
        actions
    else
        if not apply then
            tracefn "Migration plan (dry run - pass --apply to perform):"
        for a in actions do
            match a with
            | UpdateToolManifest p ->
                if apply then
                    if rewriteToolManifest faketVersion p then
                        tracefn "  updated tool manifest %s (paket -> faket %s)" p faketVersion
                else tracefn "  would rewrite tool manifest %s (paket -> faket %s)" p faketVersion
            | RefreshRestoreTargets r ->
                if apply then
                    RestoreProcess.extractElement r "Paket.Restore.targets" |> ignore
                    invalidateRestoreCache r
                    tracefn "  refreshed .paket/Paket.Restore.targets (faket-aware) and reset restore cache"
                else tracefn "  would refresh .paket/Paket.Restore.targets (faket-aware)"
            | RemoveLegacyFile p ->
                if apply then (try File.Delete p with _ -> ()); tracefn "  removed legacy %s" p
                else tracefn "  would remove legacy %s" p
            | ManualReview d ->
                traceWarnfn "  MANUAL: %s" d
        if apply then
            tracefn "Migration complete. Run 'dotnet tool restore' then build."
            tracefn "paket.dependencies / paket.lock / paket.references are kept as-is."
        actions
