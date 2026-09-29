# 002: Worker sandbox configuration

**Status:** accepted, for the normal launch and under MO2 2.5.2 (see "MO2").

## Decision

`AppContainerLauncher` starts `SafePatch.Worker.exe` with the following settings:

- **AppContainer, not LPAC, by default.** The profile is `SafePatch.Worker` and it has zero capabilities, so it has
  no network and no user files. LPAC is available via `SandboxOptions.LessPrivileged`, but it also needs an explicit
  ACL grant on the .NET install directory, which usually requires admin rights. It stays off until measured.
- **Only the worker's own directory is granted** read and execute, through an inheritable ACE for the container SID.
- **The worker is started with `DETACHED_PROCESS`.** With `CREATE_NO_WINDOW` the worker failed at startup with
  `0xC0000142` (DLL init failed). A console process in an AppContainer could not attach to its console host.
  The worker needs no console.
- **The environment is minimal and sorted:** `DOTNET_ROOT`, `LOCALAPPDATA` and `SystemRoot`. Without
  `LOCALAPPDATA`, `CreateProcess` fails with error 203, because Windows derives the container's storage folder
  from it. Nothing else from the host environment is passed on.
- **Only two handles are inherited**, the two anonymous pipe client ends, listed with
  `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`.
- **Child processes are blocked** by `PROC_THREAD_ATTRIBUTE_CHILD_PROCESS_POLICY`.
- **A Job Object** applies kill-on-close, a limit of one active process, a per-process memory cap, die-on-unhandled-exception
  and all UI restrictions. The worker is created suspended, assigned to the job, then resumed.
- **A per-run nonce** is passed on the command line and echoed in `Hello`.

## Evidence

`tests/SafePatch.Sandbox.Tests` runs a probe program inside the real worker, on Windows 11 Pro 10.0.26200 with a
normal (non-MO2) launch. It checks that each of the following is denied:

- reading a user file
- listing a user directory
- writing to a user directory
- writing to the worker's own directory
- a loopback TCP connect
- starting `cmd.exe`
- writing HKCU
- seeing `USERPROFILE`

Two more tests show that a runaway loop is killed at the policy timeout and that the memory cap kills a runaway
allocator.

## MO2

Verified with MO2 2.5.2 (`Mo2LaunchTests`): the plugins exist only in an MO2 mod folder, Synthesis runs inside
MO2's VFS, and SafePatch merges them with the worker still sandboxed. Three findings:

- **usvfs and CET.** MO2 injects usvfs into every process started under it. .NET programs built with CET shadow
  stacks (the default since .NET 9) crash with `0xC0000005` when that happens. Synthesis turns CET off for its own
  programs and the patchers it creates (`<CETCompat>false</CETCompat>`), so the generated patcher template does
  too.
- **The worker must not be injected.** It needs no VFS: the host opens every file it may read. The user adds
  `SafePatch.Worker.exe` to MO2's executables blacklist (Settings > Workarounds). The worker then starts without
  usvfs and keeps CET. Without that entry the worker crashes at startup, the run fails closed, and the host's error
  says what to change.
- **Building under MO2 crashes csc.** This is a known Synthesis problem, not ours. Users let Synthesis build once
  outside MO2; later runs reuse that build. The test does the same.

## Worker folder

The worker and its complete dependency closure live in `SafePatch.Worker\` next to the patcher
(`SafePatchHost.WorkerPath`), so the container's grant covers only that folder, not the patcher's output. The
`SafePatch.Synthesis` package ships the folder under `tools/worker` and copies it there with a `buildTransitive`
targets file. Project references get the same layout from `SafePatch.Synthesis.csproj`.
