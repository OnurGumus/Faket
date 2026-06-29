# Faket

A modern .NET 10 fork of [Paket](https://github.com/fsprojects/Paket) — a dependency manager for
.NET with support for NuGet packages and git repositories.

## What's different in Faket

Faket tracks upstream Paket but modernizes the codebase and adds a few capabilities:

- **.NET 10 only.** Dropped `net461`/`netstandard2.0`/`netcoreapp2.1`, the C# bootstrapper, ILRepack
  assembly merging and mono support. One clean `net10.0` target.
- **System.Text.Json** instead of Newtonsoft.Json.
- **Reproducible (Nix-friendly) lock files.** `faket hash` writes per-package content hashes
  (`sha512: …`) into `paket.lock`. The hashes match NuGet's authoritative `.nupkg.sha512`, so tools
  like Nix can pin packages reproducibly.
- **Tamper-evident restore.** `faket restore --verify-hashes` re-hashes each downloaded `.nupkg`
  and fails if it doesn't match the hash recorded in `paket.lock`.
- **Nix export.** `faket nix` generates a `deps.nix` (`fetchNuGet` entries with SRI `sha512-…`)
  for nixpkgs `buildDotnetModule`.
- **Migration.** `faket migrate` converts an existing Paket setup to Faket (dry run unless `--apply`).
- The CLI command is **`faket`** (the package id is `Faket`).

Internals keep the `Paket.*` namespaces so upstream fixes remain easy to merge. Everything below is
inherited Paket documentation; substitute `faket` for `paket` on the command line.

## Why Paket?

NuGet [did not]([url](https://devblogs.microsoft.com/nuget/announcing-nuget-6-3-transitive-dependencies-floating-versions-and-re-enabling-signed-package-verification/)) separate out the concept of transitive dependencies.
If you install a package into your project and that package has further dependencies then all transitive packages are included in the packages.config.
There is no way to tell which packages are only transitive dependencies.

Even more importantly: If two packages reference conflicting versions of a package, NuGet will silently take the latest version ([read more](https://fsprojects.github.io/Paket/controlling-nuget-resolution.html)). You have no control over this process.

Paket on the other hand maintains this information on a consistent and stable basis within the [`paket.lock` file][7] in the solution root.
This file, together with the [`paket.dependencies` file][8] enables you to determine exactly what's happening with your dependencies.

Paket also enables you to [reference files directly from git][9] repositories or any [http-resource][11].

For more reasons see the [FAQ][10].

## Online resources

 - [Source code][1]
 - [Documentation][2]
 - [Getting started guide](https://fsprojects.github.io/Paket/get-started.html)
 - Download [paket.exe][3]
 - Download [paket.bootstrapper.exe][3]

## Troubleshooting and support

 - Found a bug or missing a feature? Feed the [issue tracker][4].
 - Announcements and related miscellanea through Twitter ([@PaketManager][5])

## Quick contributing guide

 - Fork and clone locally.
 - Build the solution with Visual Studio, `build.cmd` or `build.sh`.
 - Create a topic specific branch in git. Add a nice feature in the code. Do not
   forget to add tests and/or docs.
 - Run `build.cmd` (`build.sh` on Mono) to make sure all tests are still
   passing.
 - When built, you'll find the binaries in `./bin` which you can then test
   with locally, to ensure the bug or feature has been successfully implemented.
 - Send a Pull Request.

If you want to contribute to the [docs][2] then please modify the markdown files in `/docs/content` and send a pull request.
Note, that short description and syntax for each command is generated automatically from the `Paket.Commands` module.

## License

The [MIT license][6]

## Maintainer(s)

- [@forki](https://github.com/forki)
- [@agross](https://github.com/agross)
- [@cloudroutine](https://github.com/cloudroutine)
- [@matthid](https://github.com/matthid)
- [@isaacabraham](https://github.com/isaacabraham)
- [@theimowski](https://github.com/theimowski)

The default maintainer account for projects under "fsprojects" is [@fsprojectsgit](https://github.com/fsprojectsgit) - F# Community Project Incubation Space (repo management)

 [1]: https://github.com/fsprojects/Paket/
 [2]: https://fsprojects.github.io/Paket/
 [3]: https://github.com/fsprojects/Paket/releases/latest
 [4]: https://github.com/fsprojects/Paket/issues
 [5]: https://twitter.com/PaketManager
 [6]: https://github.com/fsprojects/Paket/blob/master/LICENSE.txt
 [7]: https://fsprojects.github.io/Paket/lock-file.html
 [8]: https://fsprojects.github.io/Paket/dependencies-file.html
 [9]: https://fsprojects.github.io/Paket/git-dependencies.html
 [10]: https://fsprojects.github.io/Paket/faq.html
 [11]: https://fsprojects.github.io/Paket/http-dependencies.html
 [badge-pr-stats]: https://www.issuestats.com/github/fsprojects/Paket/badge/pr
 [badge-issue-stats]: https://www.issuestats.com/github/fsprojects/Paket/badge/issue
 [link-issue-stats]: https://www.issuestats.com/github/fsprojects/Paket
