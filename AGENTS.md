# SafeSynthesis

Sandboxed, agent-authored Skyrim patchers that run inside Synthesis. Design: `Synthesis_MCP_design.md`.
Plan and status: `docs/implementation-plan.md`. Decisions: `docs/decisions/`. ADR 005, on native Mutagen in the
sandbox, supersedes the design's hand-written SDK. ADR 006 covers the rest of the API: nested records,
settings, the asset provider, FormKey persistence and export options. ADR 007 covers versioning, and ADR 008 the
load-order queries (budgets, conflicts, MO2 profiles, `run_query`), and ADR 009 the load-order index they read
through. What runs where:
`docs/compatibility.md`. User docs: `README.md`.

## How a patch runs

1. The agent writes a native Synthesis patch function:
   `public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)`.
   The same code runs unchanged in a native patcher.
2. `PatchCompiler` compiles it against the real Mutagen/Synthesis assemblies, under an API allow-list.
3. Inside the AppContainer worker, the real `SynthesisPipeline` runs it over a `BrokeredFileSystem`. That
   filesystem is in memory, and its plugins, BSAs and strings files are read-only handles the host shared.
   Loose Data files are requested one at a time (`AssetRequest`), and the host's `AssetBroker` serves only
   those the manifest's `Assets` patterns allow. Settings, the FormKey persistence folder and the game INI
   (for its archive lists) are shared inline.
4. The host diffs the returned plugin against the patch using Mutagen's generated equals masks, record by
   record (nested records included, parents without their children). It enforces the manifest's `Type.Field`
   scope and its `Creatable`/`Removable` types, and checks the returned FormKey persistence file. It then
   copies the accepted records into `state.PatchMod` through their contexts, and applies removals.

The worker's plugin is an intermediate: it runs without `--Localize` and may come back split into parts. The
host reads it with the run's language and string encoding (`PluginFormat`). See ADR 006, "Export options".

## Commands

```sh
powershell -NoProfile -ExecutionPolicy Bypass -File eng/mutagen-fork/Build-MutagenFork.ps1  # once: packs the Mutagen fork
dotnet build                                                                            # warnings are errors
dotnet test --filter "Category!=Sandbox&Category!=Build&Category!=Synthesis&Category!=MO2&Category!=Scale"  # fast loop
dotnet test                                                                             # everything (Windows)
```

Test categories:

- `Sandbox` runs the real AppContainer worker.
- `Build` runs `dotnet build` on a generated patcher.
- `Synthesis` drives the Synthesis CLI.
- `Fuzz` feeds the frame channel and the committer mutated input. It is seeded and short enough for the fast
  loop; set `SAFEPATCH_FUZZ_ITERATIONS` to run longer.
- `Scale` runs a merge over a synthetic load order of hundreds of plugins in the sandbox and prints timings.
  Run it manually; results are in the implementation plan.
- `Game` runs programs that override, create and revert records of every type in a real game, in the sandbox. It
  needs `SAFEPATCH_GAME_DATA` (a Skyrim Data folder) and `SAFEPATCH_GAME_PLUGINS` (a plugins.txt) and skips without
  them. Run it after changes to the committer, `ModCodec` or `RecordDiff`.
- `MO2` launches it through Mod Organizer 2. It skips while another MO2 or usvfs-hooked process is running:
  MO2 2.5.2 shares one usvfs instance per session, so the test would join and disturb it. Under MO2 the worker
  must be on MO2's executables blacklist and patchers need `<CETCompat>false</CETCompat>` (ADR 002, "MO2").
- Processes the E2E tests start run with MSBuild node reuse and the compiler server off; otherwise Synthesis
  hangs reading `dotnet build` output until those servers idle out.

`dotnet pack src/SafePatch.Synthesis` builds the runtime package. The worker ships in `SafePatch.Worker\` next
to the patcher, the only folder the container can read (ADR 002). Nothing is published until the user approves
a release.

The pinned Synthesis CLI (built from source) and MO2 are fetched by `eng/e2e-tools/Get-E2ETools.ps1` before the
EndToEnd tests build. The tool cache is `%LOCALAPPDATA%\SafePatch\e2e-tools`. Skip the fetch with
`-p:SkipE2ETools=true`. Set `SAFEPATCH_E2E_KEEP=1` to keep a failed workspace.

## Structure rules (enforced by tests/SafePatch.Architecture.Tests)

- `Protocol` has no dependencies. `Host` depends only on `Protocol`, with no packages. It is testable with the
  fakes in `tests/SafePatch.TestSupport`.
- `Worker.Core` runs Mutagen/Synthesis but must never reference `Host` or anything with outside authority.
- Mutagen validation lives in `Mutagen`, Roslyn in `Compiler` and Win32 in `Sandbox.Windows`.
- `Synthesis` is the single composition root for a patcher run. There is no DI container, except in `Mcp`,
  where the SDK's host needs one.
- `Authoring` is the service behind the `safepatch` CLI (`Cli`) and MCP server (`Mcp`). The front ends map one
  to one onto it, add no logic of their own, and never write to a load order. Load orders come from a
  `LoadOrderSource` (a Data folder, or an MO2 profile read without MO2) through a read-only `DataView`. Queries
  (`Query/QueryService`) return a `ResultSet`, which `AuthoringSession` renders within a budget and keeps under a
  handle. Never return a whole result unbudgeted to an agent.
- Queries choose records from `Index/LoadOrderIndex`, built once per snapshot from every plugin's Mutagen overlay
  (record headers and EditorIDs only, in parallel batches), and read the versions they show with
  `LoadOrderSnapshot.Read`, through the plugin's overlay. Do not enumerate a plugin's records again in a query.
  Incoming references and asset users narrow their candidates with a raw byte search of the plugins
  (`PluginScanner`) before confirming through Mutagen. Check on a real Data folder with `tests/SafePatch.Benchmarks`
  (`bench`, `check-full`, `check-decode`, `check-refs`).
- Add an interface only when there is a second implementation, and a test fake counts. The seams are
  `IWorkerLauncher`, `IWorkerProcess`, `IPatchCommitter` and `IAssetSource`.
- A new project needs a rule in `DependencyRulesTests.Allowed`.

## Conventions

- Fail closed: throw rather than continue. There is never a fallback that runs without the sandbox.
  `InProcessWorkerLauncher` exists only in TestSupport.
- Anything that interprets records should use Mutagen's generated code (equals masks, `EnumerateFormLinks`,
  registrations) rather than per-type code, so new record types and Mutagen updates need no changes here.
- Compare records only after reading both sides the same way (`ModCodec`). Mutagen's parse of some fields
  depends on context.
- Package versions are pinned in `Directory.Packages.props`. Treat any change as a trusted-template change.
  Mutagen (Kernel, Core, Skyrim) is a build of the fork `panthuncia/Mutagen` at the commit
  `eng/mutagen-fork/Build-MutagenFork.ps1` pins, from the local feed `NuGet.config` names, until its fixes and
  performance changes are in a Mutagen release; then return to nuget.org.
- Settings sources reach trusted code (the patcher, and Synthesis's settings GUI), so `SettingsPolicyChecker`
  allows data only. Keep it strict.
- Test programs are C# source compiled at test time: `TestPrograms.Compile` skips the policy, and
  `PatchCompiler.Compile` enforces it.
