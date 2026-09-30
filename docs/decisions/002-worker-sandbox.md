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
MO2's VFS, and SafePatch merges them with the worker still sandboxed. Findings:

- **usvfs and CET.** MO2 injects usvfs into every process started under it. .NET programs built with CET shadow
  stacks (the default since .NET 9) crash with `0xC0000005` when that happens. Synthesis turns CET off for its own
  programs and the patchers it creates (`<CETCompat>false</CETCompat>`), so the generated patcher template does
  too.
- **The worker is injected, harmlessly.** It needs no VFS: the host opens every file it may read. usvfs injects
  itself by pointing a new process's first thread at a stub that loads `usvfs_x64.dll` and then returns with a plain
  `ret`. A CET shadow stack refuses that return, so the worker crashed at startup unless the user put
  `SafePatch.Worker.exe` on MO2's executables blacklist. SafePatch cannot add it for the user: usvfs's
  `usvfsBlacklistExecutable` only works in MO2's own process (in an injected one its context is null, and calling it
  crashes). So when the host runs under MO2 (usvfs is loaded in it), `AppContainerLauncher` starts the worker with
  `PROCESS_CREATION_MITIGATION_POLICY2_CET_USER_SHADOW_STACKS_ALWAYS_OFF`. The stub then runs; the container cannot
  read MO2's folder, so its `LoadLibrary` fails and it returns to the worker's normal start. Where the container can
  read it, usvfs loads but cannot open its session's shared memory from the container, and installs no hooks.
  **Trade-off:** under MO2 the worker loses CET's protection against return-oriented exploits of memory corruption.
  It runs managed code under the API allow-list, and the AppContainer, Job Object and brokered handles are
  unchanged, so this is defence in depth given up only there. A manual blacklist entry still works, and then MO2
  does not inject at all.
- **SafePatch itself runs inside MO2.** MO2 is the primary target. An MCP client or shell starts `safepatch-mcp` or
  `safepatch` directly and talks over standard streams, but a program MO2 starts is not the client's child. With
  `--mo2`, the copy the client started asks MO2 (`ModOrganizer.exe [-i <instance>] [-p <profile>] run ...`, which
  hands the request to an MO2 already running) to start `dotnet <assembly>` inside its VFS, and relays standard
  input, output, error and the exit code over a named pipe only the current user can open (`Mo2Relay`). The outside
  copy checks the pipe's client is the program it asked MO2 to start. It starts `dotnet`, not the tool launcher
  `dotnet tool install` writes, because that launcher is built for CET and would crash like the worker; the
  projects themselves set `<CETCompat>false</CETCompat>` for when MO2 starts their executables directly. Inside,
  the game's Data folder shows MO2's whole view: `DataView` counts a file as the game's only if it really lives
  there (`GetFinalPathNameByHandle` gives the file usvfs opened), and the profile defaults to the one MO2 runs,
  found from the plugins.txt MO2 maps over the game's. `--no-vfs` reads the profile without MO2, as before.
- **Building under MO2 crashes csc.** This is a known Synthesis problem, not ours. Users let Synthesis build once
  outside MO2; later runs reuse that build. The test does the same.

## Worker folder

The worker and its complete dependency closure live in `SafePatch.Worker\` next to the patcher
(`SafePatchHost.WorkerPath`), so the container's grant covers only that folder, not the patcher's output. The
`SafePatch.Synthesis` package ships the folder under `tools/worker` and copies it there with a `buildTransitive`
targets file. Project references get the same layout from `SafePatch.Synthesis.csproj`.
