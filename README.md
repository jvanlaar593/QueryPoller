# Query Poller for SSMS

An SSMS 22 extension that re-runs a T-SQL query every *N* seconds — handy for watching blocking, job progress, queue depth, replication latency, etc. It has two modes:

| | **Execute with Polling** (toolbar) | **Query Poller window** |
|---|---|---|
| Where | Button on the SQL Editor toolbar (and **Query** menu) | **Tools → Query Poller** / **View → Other Windows** |
| Runs in | The query window itself, via SSMS's normal Execute | Its own connection, cloned from a query window |
| Results | SSMS's usual Results/Messages tabs | A grid in the tool window |
| Session state | Same session as the window (temp tables, `USE`, `SET` options carry over) | Separate session; the query window is untouched |
| Settings | **Tools → Options → Query Poller → Execute with Polling** | In the tool window |
| Auto-stop when results stop changing | Yes (Results to Grid) | Yes |

## Execute with Polling (toolbar mode)

1. Open a query window, connect it, and write your query.
2. Optionally select part of the script — only the selection is polled, just like **Execute**.
3. Click **Execute with Polling** on the SQL Editor toolbar (next to **Execute**), or use **Query → Execute with Polling**.
4. The query runs immediately and then every *N* seconds. Click the button again (**Stop Polling**) to stop.

Each query window polls independently; the button reflects the state of the active window. Progress is shown in the SSMS status bar.

Settings (**Tools → Options → Query Poller → Execute with Polling**), applied when polling starts:

- **Interval (seconds)** — start-to-start, minimum 1 (default 5). Runs never overlap.
- **Stop on error** — stop when a run finishes with an error (default off).
- **Maximum runs** — stop after this many runs; 0 = until stopped (default 0).
- **Stop when results unchanged for (seconds)** — stop once the grid results have stayed identical for this long, e.g. when waiting for a job or blocking to clear; 0 = never (default 30). See [Auto-stop on unchanged results](#auto-stop-on-unchanged-results).

Polling also stops when you press SSMS's **Cancel Executing Query**, close or disconnect the window, or if a run can't start. Stopping doesn't cancel a run that's already in progress.

To make sure each run executes the same text:

- If you started with a **selection**, that selection is re-applied before every run (it's tracked through edits elsewhere in the script). Editing the selected text itself stops polling.
- If you started with **no selection**, any selection you make later is cleared before the next run so the whole script runs. Edits to the script are picked up by the next run, just like pressing Execute.
- If you started a run yourself, the poller waits for it to finish.
- Keyboard focus is restored after each run, so polling a background window doesn't steal focus.

## Query Poller window

- **Connection from:** pick any connected query window. The poller opens its *own* connection with the same server, login, auth method (Windows, SQL, all Microsoft Entra ID modes), encryption and advanced options — your query window's session is never touched.
- **Copy query from window** copies the selected text (or the whole script if nothing is selected).
- Interval in seconds, command timeout, **Auto-stop after N sec unchanged** (default 30, 0 = never), optional **Stop on error**, **Start/Stop** and **Run now**.
- Polls never overlap; the interval is measured start-to-start.
- Scripts can contain `GO` / `GO n` batch separators and return multiple result sets (pick one from the *Result set* dropdown).
- Sort order and scroll position are kept between refreshes when the result shape doesn't change.
- Status line shows run number, time, duration, row counts, rows affected and errors; the tab caption shows the server and interval while polling.
- Each result set is capped at 10,000 rows.
- Closing the tool window stops polling. Query, interval, timeout, auto-stop and stop-on-error are remembered.
- Poller sessions show up as `SSMS Query Poller` in `program_name`, so they're easy to spot (or exclude) in `sys.dm_exec_sessions`.

Usage:

1. In SSMS, open a query window and connect it to a server.
2. Open the poller from **Tools → Query Poller** or **View → Other Windows → Query Poller**. It's a normal tool window, so you can dock it anywhere (e.g. as a tab next to your query window).
3. In **Connection from:**, pick the connected query window whose connection you want to use. The active window is selected automatically when the poller first opens.
4. Type a query, or click **Copy query from window**. Optionally change the database.
5. Set the interval (seconds) and press **Start**. Press **Stop** to stop, or **Run now** to skip the remaining wait.

## Auto-stop on unchanged results

Both modes can stop polling by themselves once the results stop changing. Set the number of seconds in **Tools → Options → Query Poller → Execute with Polling** (toolbar mode) or in the **Auto-stop after** box (tool window).

- After each successful run, the results are compared with the previous run: every result set's column names, row count and cell values. Messages (`PRINT`, row counts) aren't compared.
- The unchanged time is measured between run start times, so polling stops on the first run whose results have been identical for at least that long. If it's shorter than the interval, polling stops as soon as two runs in a row match.
- A failed run restarts the unchanged period.
- Results that always change (e.g. `SELECT GETDATE()`) never trigger it.
- The tool window shows progress (*unchanged 20s of 30s*) next to the countdown; toolbar mode shows it in the SSMS status bar.

In toolbar mode the comparison reads the result grids SSMS displays, so it needs **Results to Grid** and is limited to 250,000 cells per run. If it can't compare (Results to Text/File, larger results, or an SSMS update changed its internals), polling simply continues and the status bar says why auto-stop is inactive. In the tool window it compares the (up to 10,000) rows per result set that the window fetches.

## Why .NET Framework 4.8?

SSMS 22 runs on .NET Framework, and this extension is loaded into the SSMS process so it can reuse a query window's connection (including every Entra ID auth mode) through SSMS's own APIs. An in-process extension must match the host runtime, so it can't target .NET 8/10. The code still uses the latest C# and SDK-style project tooling.

## Requirements

- SSMS 22 (x64)
- To build: Visual Studio 2026 (18.x) or Build Tools with MSBuild, and the .NET Framework 4.8 targeting pack. The VS SDK comes from NuGet, so the *Visual Studio extension development* workload isn't required.
- The project compiles against SSMS assemblies from `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE`. If SSMS is installed elsewhere, pass `/p:SsmsIdeDir=<path to Common7\IDE>`.

## Build

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" `
    src\SsmsQueryPoller\SsmsQueryPoller.csproj /restore /p:Configuration=Release
```

Output: `src\SsmsQueryPoller\bin\Release\net48\SsmsQueryPoller.vsix`

## Install

Close SSMS, then run SSMS's own VSIX installer:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    src\SsmsQueryPoller\bin\Release\net48\SsmsQueryPoller.vsix
```

To uninstall, use **Extensions â†’ Manage Extensions** in SSMS, or:

```powershell
& "C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\VSIXInstaller.exe" `
    /uninstall:SsmsQueryPoller.362889d2-5c2e-42be-9ad2-8049a72ac586
```

## Tests

The query engine (`QueryExecutor`) is tested against SQL Server LocalDB (`(localdb)\MSSQLLocalDB`):

```powershell
dotnet test tests\SsmsQueryPoller.Tests
```

## Notes

- **Execute with Polling** drives SSMS's query window through internal (non-public) SSMS members. If a future SSMS update changes them, the button reports that the SSMS version isn't supported; the Query Poller window doesn't depend on them and keeps working.
- With **Results to File**, SSMS asks for a file name on every run, so use Results to Grid or Text for polling.
- In the Query Poller window:
  - The database box is pre-filled from the query window's connection settings (its initial database). If you switched databases in that window with `USE` or the toolbar dropdown, set the database here explicitly.
  - `GO` detection is line-based (like `sqlcmd`), so a line containing only `GO` inside a block comment or string literal is still treated as a separator.
  - `PRINT` / informational messages aren't shown; errors are shown in the status line.
