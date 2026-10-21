<div align="center">

# Query Poller for SSMS

**Re-run a T-SQL query on an interval — right inside SQL Server Management Studio.**

[![SSMS 22](https://img.shields.io/badge/SSMS-22-CC2927?logo=microsoftsqlserver&logoColor=white)](https://learn.microsoft.com/sql/ssms/)
[![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/download/dotnet-framework/net48)
[![C#](https://img.shields.io/badge/C%23-latest-239120?logo=csharp&logoColor=white)](#)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](src/SsmsQueryPoller/LICENSE.txt)

</div>

---

Watching blocking chains clear, a SQL Agent job progress, a queue drain or replication catch up usually means pressing **F5** over and over. Query Poller does it for you: pick an interval, and it re-executes your query until you stop it — or until the results stop changing.

## Contents

- [Highlights](#highlights)
- [Two ways to poll](#two-ways-to-poll)
- [Getting started](#getting-started)
- [Usage](#usage)
- [Configuration](#configuration)
- [Auto-stop on unchanged results](#auto-stop-on-unchanged-results)
- [How it works](#how-it-works)
- [Limitations](#limitations)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [License](#license)

## Highlights

- **One-click polling from the SQL Editor toolbar** — *Execute with Polling* sits right next to *Execute* and re-runs the query window natively, so results land in SSMS's familiar Results and Messages tabs.
- **Dedicated Query Poller window** — a dockable tool window with its own results grid that polls on a separate connection and never touches your query window's session.
- **Auto-stop** — stop automatically once the results have been identical for a set time (30 seconds by default), so a finished job doesn't keep getting polled.
- **Every authentication mode** — Windows, SQL Server and all Microsoft Entra ID modes, because connections are created with SSMS's own connection factory.
- **Safe by design** — runs never overlap, the polled text stays pinned to what you started with, and keyboard focus is never stolen by a background window.
- **Stop conditions** — stop on error, after a maximum number of runs, on cancel, or when the window closes or disconnects.

## Two ways to poll

| | **Execute with Polling** (toolbar) | **Query Poller window** |
|---|---|---|
| **Open from** | SQL Editor toolbar · **Query** menu | **Tools → Query Poller** · **View → Other Windows** |
| **Runs in** | The query window, via SSMS's own Execute | A separate connection cloned from a query window |
| **Results** | SSMS Results / Messages tabs | Grid inside the tool window |
| **Session state** | Shared with the window — temp tables, `USE`, `SET` options carry over | Isolated — the query window is untouched |
| **Settings** | **Tools → Options → Query Poller** | Inline in the tool window |
| **Auto-stop** | ✔ (Results to Grid) | ✔ |
| **Best for** | Quick ad-hoc monitoring of the script you're already in | A long-running monitor alongside your normal work |

## Getting started

### Requirements

| | |
|---|---|
| **Run** | SQL Server Management Studio 22 (x64) |
| **Build** | Visual Studio 2026 (18.x) or Build Tools 2026 with MSBuild · .NET Framework 4.8 targeting pack |
| **Test** | SQL Server Express LocalDB (`(localdb)\MSSQLLocalDB`) |

The Visual Studio SDK is restored from NuGet, so the *Visual Studio extension development* workload is **not** required.

### Build

```powershell
git clone https://github.com/jvanlaar593/QueryPoller.git
cd QueryPoller

& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    src\SsmsQueryPoller\SsmsQueryPoller.csproj /restore /p:Configuration=Release
```

The extension package is written to `src\SsmsQueryPoller\bin\Release\net48\SsmsQueryPoller.vsix`.

> [!NOTE]
> The project compiles against assemblies from your SSMS installation. If SSMS isn't installed in the default location, add `/p:SsmsIdeDir="<SSMS folder>\Common7\IDE"`.

### Install

Close SSMS, then run SSMS's own VSIX installer:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    src\SsmsQueryPoller\bin\Release\net48\SsmsQueryPoller.vsix
```

Installing a newer version upgrades in place — there's no need to uninstall first.

### Uninstall

Use **Extensions → Manage Extensions** in SSMS, or:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /uninstall:SsmsQueryPoller.362889d2-5c2e-42be-9ad2-8049a72ac586
```

## Usage

### Execute with Polling

1. Open a query window, connect it, and write your query.
2. *(Optional)* Select part of the script — just like **Execute**, only the selection is polled.
3. Click **Execute with Polling** on the SQL Editor toolbar, or choose **Query → Execute with Polling**.
4. The query runs immediately and then on every interval. Click **Stop Polling** (the same button) to stop.

Each query window polls independently and the button reflects the state of the active window. Progress — run number, next run time and auto-stop status — is shown in the SSMS status bar.

### Query Poller window

1. Open a query window and connect it to a server.
2. Open **Tools → Query Poller** (or **View → Other Windows → Query Poller**) and dock it wherever you like.
3. In **Connection from**, choose the query window whose connection to use. The active window is preselected.
4. Type a query or click **Copy query from window**, and optionally change the database.
5. Set the interval and press **Start**. **Stop** ends polling; **Run now** skips the remaining wait.

The tool window supports `GO` / `GO n` batch separators and multiple result sets (switch between them with the **Result set** dropdown). Your sort order and scroll position are preserved between refreshes, and the status line reports the run number, duration, row counts, rows affected and any errors.

## Configuration

### Execute with Polling

**Tools → Options → Query Poller → Execute with Polling**. Settings are applied when polling starts.

| Setting | Default | Description |
|---|---|---|
| **Interval (seconds)** | `5` | Time between run starts. Minimum 1. Runs never overlap — a slow run simply delays the next one. |
| **Stop on error** | Off | Stop when a run finishes with an error. The error stays in the Messages tab. |
| **Maximum runs** | `0` | Stop after this many runs. `0` runs until stopped. |
| **Stop when results unchanged for (seconds)** | `30` | Stop once the grid results have been identical for this long. `0` disables. |

Polling also stops when you press **Cancel Executing Query**, close or disconnect the window, or when a run can't start. Stopping never cancels a run that's already in progress.

### Query Poller window

| Field | Default | Description |
|---|---|---|
| **Connection from** | Active window | Query window whose server, login, authentication and connection options are cloned. |
| **Database** | Window's initial database | Database the query runs in. |
| **Every … sec** | `5` | Polling interval, start-to-start. |
| **Timeout … sec** | `30` | Command timeout. `0` = no timeout. |
| **Auto-stop after … sec unchanged** | `30` | Stop once results have been identical for this long. `0` disables. |
| **Stop on error** | Off | Stop when a run fails. |

The query and all fields are remembered between sessions. Closing the tool window stops polling.

## Auto-stop on unchanged results

Both modes can end polling on their own once there's nothing new to see.

- After each successful run, every result set's **column names, row count and cell values** are compared with the previous run. Messages (`PRINT`, row counts) are not compared.
- The unchanged time is measured between **run start times**; polling stops on the first run whose results have been identical for at least the configured period. If the period is shorter than the interval, polling stops as soon as two consecutive runs match.
- A **failed run** restarts the unchanged period.
- Results that change on every run — such as `SELECT GETDATE()` — never trigger auto-stop.
- Progress (*unchanged 20s of 30s*) is shown next to the countdown in the tool window, and in the status bar in toolbar mode.

In toolbar mode the comparison reads the result grids SSMS is displaying, so it requires **Results to Grid** and compares up to **250,000 cells** per run. If the results can't be compared — Results to Text or File, a larger result, or a future SSMS change — polling simply continues and the status bar explains why auto-stop is inactive. The tool window compares the rows it fetches (up to 10,000 per result set).

## How it works

### Execute with Polling

SSMS routes **Execute** to whichever window has keyboard focus. To make background polling safe, Query Poller talks to the polled window directly:

- **Right window, every time** — Execute is invoked on the polled window's own command handler, never through focus-based routing, so typing in another window can't redirect a run.
- **Stable query text** — if you started with a selection, it's tracked through edits elsewhere in the script and re-selected before each run; editing the selected text itself stops polling. If you started without a selection, any new selection is cleared so the whole script runs.
- **No focus stealing** — SSMS focuses the editor it executes; keyboard focus is restored immediately afterwards.
- **No overlap** — if you start a run yourself, the poller waits for it to finish.

### Query Poller window

The tool window clones the selected query window's connection using SSMS's own connection factory, so every authentication mode and connection option behaves exactly as it does in the query window. Each poll opens a pooled connection tagged with the application name **`SSMS Query Poller`**, making poller sessions easy to identify — or exclude — in `sys.dm_exec_sessions`.

### Why .NET Framework 4.8?

SSMS 22 runs on .NET Framework, and Query Poller is loaded into the SSMS process so it can use SSMS's own editor and connection APIs. An in-process extension must match its host's runtime, so it targets .NET Framework 4.8 — while still using the latest C# language version and SDK-style project tooling.

## Limitations

- **Execute with Polling** relies on SSMS internals that aren't part of a public API. If a future SSMS update changes them, the command reports that the SSMS version isn't supported; the Query Poller window doesn't depend on them and keeps working.
- With **Results to File**, SSMS prompts for a file name on every run — use Results to Grid or Text for polling.
- In the Query Poller window:
  - The **Database** box is pre-filled with the query window's *initial* database. If you've since switched databases in that window, set it explicitly.
  - `GO` detection is line-based (like `sqlcmd`), so a line containing only `GO` inside a block comment or string literal is treated as a separator.
  - `PRINT` and other informational messages aren't displayed; errors appear in the status line.
  - Each result set is capped at 10,000 rows.

## Troubleshooting

| Symptom | Resolution |
|---|---|
| **Execute with Polling** isn't on the toolbar | Run `SSMS.exe /setup` once from `Common7\IDE`, then start SSMS normally to rebuild its menus. |
| **Query Poller** isn't listed under **Tools → Options** | Search for "Query Poller" in the Options search box, or look for the link to the legacy Options dialog. |
| Status bar says **auto-stop inactive** | Switch the query window to **Results to Grid** (**Ctrl+D**), or reduce the size of the result. |
| The installer says the extension is already installed | Increase the version in `source.extension.vsixmanifest` and rebuild, or uninstall first. |

## Development

```text
src/SsmsQueryPoller/
├── QueryPollerPackage.cs / .vsct   Package registration, commands, toolbar and menu placement
├── ExecuteWithPolling.cs           Toolbar mode: polling sessions and stop conditions
├── SqlEditorAdapter.cs             Bridge to SSMS query windows (execute, state, result grids)
├── PollingOptionsPage.cs           Tools → Options page
├── QueryPollerToolWindow.cs        Tool window host
├── QueryPollerControl.xaml(.cs)    Tool window UI and polling loop
├── SsmsQueryEditors.cs             Query window discovery and connection cloning
├── QueryExecutor.cs                Batch splitting and query execution
├── ResultChangeTracker.cs          Result fingerprints and auto-stop logic
└── PollerSettings.cs               Persisted tool window settings
tests/SsmsQueryPoller.Tests/        xUnit tests (run against LocalDB)
```

Run the tests:

```powershell
dotnet test tests\SsmsQueryPoller.Tests
```

The tests cover batch splitting, query execution, result formatting, timeouts and cancellation, result fingerprints and auto-stop timing.

## License

Released under the [MIT License](src/SsmsQueryPoller/LICENSE.txt).
