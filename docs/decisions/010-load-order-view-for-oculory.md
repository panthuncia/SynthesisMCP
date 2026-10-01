# 010: A load-order view for Oculory

**Status:** accepted, October 2026, jointly with Oculory's ADR 012. Built when Oculory wires SafePatch's authoring tools
into its assistant (Oculory's M5); nothing here changes SafePatch's code yet.

## Context

Oculory is a record editor that runs SafePatch's programs as its scripts and will offer SafePatch's authoring tools
(`find_records`, `get_record`, `find_conflicts`, `references`, `run_query`, `test_patch`, ...) to its assistant. Both
must see the load order as Oculory has it, with edits the user hasn't saved.

Running programs needs nothing new. Oculory writes each plugin with unsaved edits into a temporary folder (the same
splice its save uses) and shares it with the worker in the plugin's place, as `SynthesisInputs.Plan` shares an MO2 mod's
plugin at its path in the Data folder; it builds the `PatchSession` and `VerifiedPackage` itself and diffs the output
with `ModCodec` and `RecordDiff` into its own change set. Oculory packs SafePatch's libraries from a pinned commit of
this repository, which needs no packaging work here.

The queries are another matter. `QueryService` and `AuthoringSession` read a `LoadOrderSnapshot`: its Mutagen mods and
link cache (about ten places) and its `LoadOrderIndex`, built once per snapshot (ADR 009: 0.4–0.7 s on vanilla, about
80 bytes a record). Handing them a snapshot of files written with the edits would rebuild that index for every change
the user makes, and hold a second index of the load order beside Oculory's, which already has the edits layered on and
updates in milliseconds per change, and already has every record's incoming references.

## Decision

**`SafePatch.Authoring` reads load orders through a view, which `LoadOrderSnapshot` implements and Oculory implements
over its own index.** The view has what the queries use today:

- the release, the plugins in order with their masters, a generation (for result handles), and why it's stale, if it is;
- each record's FormKey, signature, EditorID and deleted flags, and its versions, each with its plugin and the record
  it's nested in;
- lookups by FormKey and by EditorID pattern, and the records of a type or a plugin;
- reading one version of a record, the same way for every version so versions compare (`ModCodec`'s rule);
- incoming references where the view has them (Oculory's reference graph); otherwise the queries keep `PluginScanner`'s
  raw search, which needs plugin files;
- the Data folder's files, for asset queries and for the inputs of the programs `test_patch` and `run_query` run.

Queries move from the snapshot's mods and link cache to the view; `LoadOrderIndex` becomes `LoadOrderSnapshot`'s
implementation detail. Programs the tools run take their worker inputs from the view's owner, so for Oculory a test run
sees the unsaved edits as a script does.

## Consequences

- The CLI and the MCP server are unchanged: they keep `LoadOrderSnapshot`, now behind the view.
- Queries that resolve links through Mutagen's link cache (`get_record`'s link names, `references`' confirmation) go
  through the view's reads instead; anything the view can't answer has to be added to it, not read around it.
- Oculory pins a SafePatch commit; a change this view needs is a commit here and a new pin there.