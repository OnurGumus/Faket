/// Populates paket.lock with per-package content hashes (base64 SHA512 of each .nupkg),
/// enabling reproducible pinning by external tooling such as Nix.
module Paket.HashProcess

open Paket
open Paket.Domain
open Paket.PackageResolver
open Paket.PackageSources
open Paket.Logging

/// Downloads (or reuses the cache for) every resolved NuGet package and records the
/// base64 SHA512 of its .nupkg into each ResolvedPackage. Path-based (local) sources are
/// left untouched since there is nothing stable to pin. Idempotent: re-running recomputes
/// the same hashes.
let addContentHashes (lockFile:LockFile) : LockFile =
    let root = lockFile.RootPath

    let hashPackage groupName (pkg:ResolvedPackage) =
        match pkg.Source with
        | LocalNuGet _ -> pkg
        | _ ->
            try
                let fileName, _ =
                    NuGet.DownloadAndExtractPackage(
                        None, root, false, PackagesFolderGroupConfig.Default, pkg.Source, [],
                        groupName, pkg.Name, pkg.Version, pkg.Kind, false, false, false, false)
                    |> Async.RunSynchronously
                { pkg with ContentHash = Some (Utils.getSha512File fileName) }
            with exn ->
                traceWarnfn "Could not hash %O %O: %s" pkg.Name pkg.Version exn.Message
                pkg

    let groups =
        lockFile.Groups
        |> Map.map (fun groupName group ->
            { group with
                Resolution = group.Resolution |> Map.map (fun _ pkg -> hashPackage groupName pkg) })

    LockFile(lockFile.FileName, groups)

/// Loads the lock file next to the given paket.dependencies, fills in content hashes and
/// saves it. Returns the updated lock file.
let Run (dependenciesFileName:string) : LockFile =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
    let lockFile = LockFile.LoadFrom lockFileName.FullName
    tracefn "Computing content hashes for %s" lockFileName.FullName
    let updated = addContentHashes lockFile
    updated.Save() |> ignore
    tracefn "Wrote content hashes to %s" lockFileName.FullName
    updated
