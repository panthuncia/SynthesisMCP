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

| Operation | CLI | MCP tool |
| --- | --- | --- |
| List the load order | `load-order` | `get_load_order` |
| Print a record's winning version | `record <FormKey or EditorID>` | `get_record` |
| Show every version and what each plugin changed | `chain <id>` | `get_override_chain` |
| Find records of a type | `query <Type> [--editor-id <text>]` | `query_records` |
| Find records linking to a record | `refs <id>` | `find_references` |
| Check a program against the policy | `validate <program.cs>` | `validate_patch` |
| Run it in the sandbox and see each field before and after | `test <program.cs> --writable ...` | `test_patch` |
| Write a Synthesis patcher repository | `package <program.cs> --name ... --out ...` | `package_synthesis_patcher` |

Load-order commands need `--data <Skyrim Data folder> --plugins <plugins.txt>`. Test runs happen in the sandbox
against that load order and write nothing to it. Run `safepatch` with no arguments for the full usage.

To use the MCP server with an MCP client such as Claude Code:

```sh
claude mcp add safepatch -- safepatch-mcp --data "C:\Games\Skyrim Special Edition\Data" --plugins "C:\Users\you\AppData\Local\Skyrim Special Edition\plugins.txt"
```

With MO2, the load order lives in MO2's virtual Data folder and profile. Authoring against it is not tested yet.

## Building

```sh
dotnet build                  # warnings are errors
dotnet test --filter "Category!=Sandbox&Category!=Build&Category!=Synthesis&Category!=MO2&Category!=Scale"   # fast loop
dotnet test                   # everything; Windows only
dotnet pack src/SafePatch.Synthesis   # the runtime package generated patchers reference
```

The design is in [Synthesis_MCP_design.md](Synthesis_MCP_design.md), the decisions are in
[docs/decisions](docs/decisions), and the status is in [docs/implementation-plan.md](docs/implementation-plan.md).
Nothing is published to nuget.org yet. Until a release, generated patchers need the locally packed runtime.
