# 003: Effective view and commit

**Status:** accepted and verified against the Synthesis CLI (0.36.6). Revised for ADR 005: the record source,
typed SDK and `SkyrimPatchTarget` this ADR first described were removed.

## Decision

- **Effective view.** The program sees what Synthesis gives a native patcher: the worker runs the real
  `SynthesisPipeline` with the same arguments and files, so it builds the same load order, link cache and patch
  mod. The patch mod is seeded with the output of earlier patchers in the group (`--SourcePath`), so their edits
  are visible and win. The E2E tests confirm this in both directions: SafePatch sees earlier patchers' records, and
  a later patcher in the group (the visibility probe) sees SafePatch's merge.
- **Pre-run snapshot.** The worker reads through read-only handles and inline copies the host made before the
  run. Nothing the program does in the worker is visible to the host until the host validates it.
- **Validate everything, then apply.** `MutagenPatchCommitter` validates the whole output first: every added,
  changed and removed record, links, limits and the FormKey persistence file. Any failure rejects the run and
  leaves `state.PatchMod` untouched. Only then does it apply the records, the removals, `NextFormID` and the
  persistence file.
- **Untrusted plugin bytes.** The host parses the worker's plugin in its own process, outside the sandbox's
  timeout, so a malformed plugin must be rejected quickly:
  - `PluginFraming` first checks every group and record length against its parent, bounds group nesting, and
    caps a compressed record's claimed size. Fuzzing found that Mutagen's reader loops forever on a
    zero-length group.
  - Mutagen reads lazily, so any other read error during validation becomes a `PatchRejectedException`.
  - `MalformedOutputTests` fuzzes the committer with mutated real outputs, with a watchdog that fails on a hang.
- **Unexposed data is kept.** Records are copied whole from the worker's output, so data the program never
  touched round-trips. The diff makes sure only fields the manifest allows changed.

## Export failure

After SafePatch commits, the rest is Synthesis's own export path, the same as for a native patcher. The E2E
test `When_Synthesis_cannot_write_the_output_after_a_commit_the_run_fails_and_the_old_file_stays` makes the
group's output file read-only and observes:

- The patcher (and SafePatch inside it) finishes normally and writes its plugin into Synthesis's temporary
  workspace (`%TEMP%\Synthesis\...\Workspace`).
- Synthesis then copies the group's result to the output folder. The copy throws `UnauthorizedAccessException`,
  and the CLI exits with an error.
- The old output file is unchanged, so a failed export never leaves a half-written plugin.
- The FormKey persistence file was written at commit time. That matches a native patcher, whose allocator
  commits when Synthesis disposes the state, which happens even when the write fails. The FormKeys it records
  are reused on the next run, so nothing is lost.
