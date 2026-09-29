# 004: Wire format

**Status:** accepted; the framing and strict parsing still apply. ADR 005 replaced the message set with `Hello → Start → Submit | Failed → Result`, which carries shared file handles and the output plugin.

## Decision

Each message is a frame: a 4-byte little-endian length followed by a UTF-8 JSON payload. The payload uses
`System.Text.Json` with these options (`FrameChannel.JsonOptions`):

- polymorphic messages with a `$kind` discriminator; unknown discriminators are rejected
- `UnmappedMemberHandling.Disallow`, `RespectNullableAnnotations` and `RespectRequiredConstructorParameters`
- enums as strings only, with integer values rejected
- `MaxDepth = 16` and a 16 MiB frame limit

The session is `Hello → Start → (Query → Reply)* → Submit | Failed → Result`. The host enforces a query budget
and a timeout. When the timeout fires, the host kills the worker, which unblocks the pending read.

## Why not binary

JSON is strict enough for the security properties, easy to inspect in tests, and needs no custom codec. The design
calls for a binary format. Revisit that once profiling at large load orders shows serialization matters (design §10).

## Deliberately absent

The transaction does not carry `programHash`, `settingsHash` or `inputFingerprint`. The worker is untrusted and
the host already knows these values, so it would have to recompute them anyway. The report records them instead.
`CreateRecord` is also absent, until FormKey allocation across reruns is settled (design §12.4).
