# 007: Versioning

**Status:** accepted, September 2026.

## Context

A SafePatch patcher has three parts that must agree:

- the payload (`manifest.json` and the compiled program);
- the host, which validates the payload and the worker's output;
- the worker, which runs the program.

The host and the worker must also parse plugins with the same Mutagen. Otherwise a record could read
differently on each side, and the diff would report changes that are not there or miss real ones.

Supporting several manifest schemas or protocol versions at once would multiply what the validator has to get
right. It would buy nothing, because a patcher is regenerated from its program in seconds.

## Decision

- **One unit of versioning.** The `SafePatch.Synthesis` package carries the host assemblies and the worker
  (`tools/worker`). They are built together, so the protocol version always matches inside one package.
- **Patchers pin their runtime exactly.** A generated patcher references `SafePatch.Synthesis` with an exact
  range (`[x.y.z]`), so an update never reaches an existing patcher silently. Upgrading means regenerating the
  patcher with the new runtime. The program and its manifest scope carry over unchanged.
- **Exact Mutagen and Synthesis.** The package depends on `Mutagen.Bethesda.Skyrim` and
  `Mutagen.Bethesda.Synthesis` with exact ranges. The worker's copies ship in its own folder, built from the same
  pins.
- **One schema, one protocol.** The runtime reads only its own manifest schema
  (`Manifest.CurrentSchemaVersion`) and speaks only its own protocol (`ProtocolVersion.Current`). A mismatch
  fails with both versions named, for example: "Manifest schema 4 is not supported: this SafePatch runtime reads
  schema 3", or "Worker speaks protocol 4, expected 3".
- **Package versions follow SemVer.** A change to the manifest schema, the protocol, the pinned
  Mutagen/Synthesis or the API allow-list is at least a minor version, since existing programs may need
  regenerating or may no longer compile. Nothing is published to nuget.org until the release is approved
  (see the implementation plan, step 10).

## Consequences

- Before 1.0, schema and protocol numbers can change without migration code. Nothing needs to be supported
  side by side.
- A user who updates Synthesis itself keeps working patchers. Synthesis talks to patchers through their command
  line, and each patcher runs the Synthesis library it was built with.
- Security fixes reach users only through regenerated patchers. The README says so.
