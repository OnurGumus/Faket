/// Populates and verifies per-package content hashes (base64 SHA512 of each .nupkg) in
/// paket.lock, enabling reproducible pinning (e.g. Nix) and tamper-evident restores.
module Paket.HashProcess

open Paket
open Paket.Domain
open Paket.PackageResolver
open Paket.PackageSources
open Paket.Logging

/// A package whose downloaded .nupkg did not match the hash recorded in paket.lock.
type HashMismatch =
    { Group : GroupName
      Package : PackageName
      Version : SemVerInfo
      Expected : string
      Actual : string }

/// Downloads (or reuses the cache for) a resolved NuGet package and returns the base64
/// SHA512 of its .nupkg. Returns None for path-based (local) sources, which have nothing
/// stable to pin.
let tryComputeHash root groupName (pkg:ResolvedPackage) : string option =
    match pkg.Source with
    | LocalNuGet _ -> None
    | _ ->
        let fileName, _ =
            NuGet.DownloadAndExtractPackage(
                None, root, false, PackagesFolderGroupConfig.Default, pkg.Source, [],
                groupName, pkg.Name, pkg.Version, pkg.Kind, false, false, false, false)
            |> Async.RunSynchronously
        Some (Utils.getSha512File fileName)

/// Fills in (or refreshes) the SHA512 content hash on every resolved NuGet package.
/// Idempotent: re-running recomputes the same hashes.
let addContentHashes (lockFile:LockFile) : LockFile =
    let root = lockFile.RootPath

    let hashPackage groupName (pkg:ResolvedPackage) =
        try
            match tryComputeHash root groupName pkg with
            | Some h -> { pkg with ContentHash = Some h }
            | None -> pkg
        with exn ->
            traceWarnfn "Could not hash %O %O: %s" pkg.Name pkg.Version exn.Message
            pkg

    let groups =
        lockFile.Groups
        |> Map.map (fun groupName group ->
            { group with
                Resolution = group.Resolution |> Map.map (fun _ pkg -> hashPackage groupName pkg) })

    LockFile(lockFile.FileName, groups)

/// Re-downloads/reads each package recorded with a hash and compares it against the lock.
/// Returns the mismatches (empty = everything verified). Packages without a recorded hash
/// are skipped and counted in `skipped`.
let verifyContentHashes (lockFile:LockFile) : HashMismatch list * int =
    let root = lockFile.RootPath
    let mutable skipped = 0
    let mismatches =
        [ for KeyValue(groupName, group) in lockFile.Groups do
            for KeyValue(_, pkg) in group.Resolution do
                match pkg.ContentHash with
                | None -> skipped <- skipped + 1
                | Some expected ->
                    match (try tryComputeHash root groupName pkg with exn -> Some ("<error: " + exn.Message + ">")) with
                    | Some actual when actual <> expected ->
                        yield { Group = groupName; Package = pkg.Name; Version = pkg.Version
                                Expected = expected; Actual = actual }
                    | _ -> () ]
    mismatches, skipped

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

/// Verifies the lock next to the given paket.dependencies. Raises on any mismatch.
let Verify (dependenciesFileName:string) : unit =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
    let lockFile = LockFile.LoadFrom lockFileName.FullName
    let mismatches, skipped = verifyContentHashes lockFile
    let verified =
        (lockFile.Groups |> Seq.sumBy (fun g -> g.Value.Resolution.Count)) - skipped - mismatches.Length
    if skipped > 0 && verified = 0 && mismatches.IsEmpty then
        traceWarnfn "No content hashes found in %s - run 'faket hash' first." lockFileName.FullName
    match mismatches with
    | [] -> tracefn "Content hash verification passed: %d package(s) verified, %d without recorded hash." verified skipped
    | _ ->
        for m in mismatches do
            traceErrorfn "  HASH MISMATCH %O/%O %O%s    expected: %s%s    actual:   %s"
                m.Group m.Package m.Version System.Environment.NewLine m.Expected System.Environment.NewLine m.Actual
        failwithf "Content hash verification FAILED for %d package(s)." mismatches.Length
