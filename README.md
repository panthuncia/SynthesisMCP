# SafePatch

SafePatch runs Skyrim patch programs inside [Synthesis](https://github.com/Mutagen-Modding/Synthesis) without
trusting them. It is built for programs an AI agent wrote. A program is an ordinary Synthesis patch function
with the full Mutagen API, but it runs in a Windows sandbox that can't touch your files, network or other
programs. Its output is checked field by field against a manifest before any of it reaches your patch.

```csharp
public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
{
    foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningContextOverrides())
    {
        // ...merge the entries other mods added...
    }
}
```

The same function works unchanged in a native Synthesis patcher.

## How it works

1. The program is compiled under an API allow-list: no file system, network, reflection, threads or native
   code.
2. A generated Synthesis patcher runs it in an AppContainer worker, with no capabilities and a memory and time
   limit. The worker runs the real Synthesis pipeline, but it can only read the plugins, archives and loose
   files the host hands it.
3. The host compares what the program produced with your patch record by record, using Mutagen's own
   comparisons. Any added record, changed field or removal the manifest does not allow rejects the whole run.
4. Only then are the changes copied into Synthesis's patch. If anything fails, Synthesis stops and nothing is
   written.

## Using a SafePatch patcher

A SafePatch patcher is a normal Synthesis patcher repository. Its `REVIEW.md` lists what it may do, and
`payload/patch.safe.cs` holds the program's source.

**Adding it to Synthesis**

- From Git: push the repository to GitHub, then in Synthesis add a *Git Repository* patcher with its URL.
- Locally: add a *Local Solution* patcher and choose the repository's `.sln`.

Synthesis builds it like any patcher, and needs the .NET 10 SDK to do so. SafePatch needs Windows 10 or 11.

**Mod Organizer 2.** Add `SafePatch.Worker.exe` to MO2's executables blacklist (Settings > Workarounds >
Executables Blacklist). The worker needs no virtual file system, because SafePatch opens every file for it, and
it can't start with MO2's hooks. Let Synthesis build the patcher once outside MO2. Building under MO2 crashes
the C# compiler, a known Synthesis issue.

**Memory.** The sandboxed worker may use up to 4 GiB. For a patcher that needs more (a large load order read in
full), set the environment variable `SAFEPATCH_WORKER_MEMORY_MB` for Synthesis, e.g. to `8192`. The authoring
CLI and MCP server also take `--worker-memory <MiB>`.

See [docs/compatibility.md](docs/compatibility.md) for tested versions and limits.

### What the manifest allows

Every patcher carries `payload/manifest.json`, shown in `REVIEW.md`:

| Field | Meaning |
| --- | --- |
| `writable` | Fields the program may change on existing records: `LeveledItem.Entries`, `Npc.*` or `*`. Names are Mutagen's class and property names. |
| `creatable` | Record types it may create, e.g. `LeveledItem`. |
| `removable` | Record types it may remove from the patch, which reverts an earlier patcher's override or deletes a record the patch added. Empty by default. |
| `assets` | Loose Data files it may read, as globs such as `meshes/**/*.nif`. Archives are always readable. |
| `settings` | Its settings class, shown in Synthesis's settings page. Settings classes may hold data only. |
| `maxRecords` | The most records it may add, change or remove. |

Everything else the program changes is rejected, even when the program asked for it.

### Reading the report

Each run prints a line such as `SafePatch 'CacoMerge': 3 record change(s).`, followed by a JSON report:

- `changes`: each record, with `formKey`, `recordType`, `editorId`, `isNew`, `isRemoved`, and the `fields` it
  changed;
- `log`: what the program printed;
- `settingsSha256`: which settings it ran with;
- `deniedAssets`: loose files it asked for that the manifest does not allow. They read as missing, and the run
  continues.

A rejected run prints the reasons (for example `changing LeveledItem.EditorID is not allowed`) and fails the
Synthesis run.

### Updating

A patcher pins the exact SafePatch version it was built with. Newer SafePatch releases, including security
fixes, reach it only when it is regenerated. Updating Synthesis itself does not require regenerating: each
patcher runs the Synthesis library it was built with.

## Authoring patchers

The `safepatch` command line and the `safepatch-mcp` server give an author (a person or an agent) the same
operations:

**Inspecting the load order**, as xEdit would:

| Operation | CLI | MCP tool |
| --- | --- | --- |
| The load order, or one plugin's header and records by type | `load-order [<plugin>]` | `load_order` |
| Find records by type, plugin, EditorID, name, field conditions or links | `find --types ... [--where "..."]` | `find_records` |
| One version of a record (the winner by default) | `record <FormKey or EditorID>` | `get_record` |
| Every version side by side, with the edits the winner loses | `compare <id>` | `compare_record` |
| Conflicts: lost edits by field and plugin, ITMs, resolutions | `conflicts --types ...` | `find_conflicts` |
| Links from and to a record | `refs <id>` | `references` |
| Where a Data file comes from, and which records use it | `asset <path>` | `find_asset` |
| Record types and their fields | `type [<Type>]` | `describe_type` |
| Anything else, as a read-only program run in the sandbox | `query <query.cs>` | `run_query` |

**Writing patchers:**

| Operation | CLI | MCP tool |
| --- | --- | --- |
| Check a program against the policy | `validate <program.cs>` | `validate_patch` |
| Run it in the sandbox and see each field before and after | `test <program.cs> --writable ...` | `test_patch` |
| Write a Synthesis patcher repository | `package <program.cs> --name ... --out ...` | `package_synthesis_patcher` |

Load-order commands need `--data <Skyrim Data folder> --plugins <plugins.txt>`, or `--mo2 <MO2 instance folder>
[--profile <name>]` for a Mod Organizer 2 profile. MO2 need not be running: SafePatch reads the profile's mod
list and layers the mod folders itself, reads only, and never stops MO2 from changing mods while it has them
open. Test runs and queries happen in the sandbox against that load order and write nothing to it. Run
`safepatch` with no arguments for the full usage.

The first query indexes every record in the load order, which takes under a second for the base game and its
Creation Club content; later queries take milliseconds. Results are sized for an agent's context. Each MCP answer fits a character budget (8,000 by default): a large
one starts with its total and group counts, shows what fits, and names the call for the rest
(`read_results`), or the whole result can be written to a file (`export_results`). After changing mods, call
`reload`. The CLI prints results in full unless given `--budget`.

To use the MCP server with an MCP client such as Claude Code:

```sh
claude mcp add safepatch -- safepatch-mcp --mo2 "C:\Games\Mod Organizer 2"
claude mcp add safepatch -- safepatch-mcp --data "C:\Games\Skyrim Special Edition\Data" --plugins "C:\Users\you\AppData\Local\Skyrim Special Edition\plugins.txt"
```

## Building

SafePatch builds against a fork of Mutagen (`panthuncia/Mutagen`) until its fixes and performance changes are in a
Mutagen release. Pack it into the local feed once before the first build, and again when its pin changes:

```sh
powershell -NoProfile -ExecutionPolicy Bypass -File eng/mutagen-fork/Build-MutagenFork.ps1
```

```sh
dotnet build                  # warnings are errors
dotnet test --filter "Category!=Sandbox&Category!=Build&Category!=Synthesis&Category!=MO2&Category!=Scale"   # fast loop
dotnet test                   # everything; Windows only
dotnet pack src/SafePatch.Synthesis   # the runtime package generated patchers reference
```

The design is in [Synthesis_MCP_design.md](Synthesis_MCP_design.md), the decisions are in
[docs/decisions](docs/decisions), and the status is in [docs/implementation-plan.md](docs/implementation-plan.md).
Nothing is published to nuget.org yet. Until a release, generated patchers need the locally packed runtime. They also
need the Mutagen fork's feed in their NuGet.config, as this repository's has it.
