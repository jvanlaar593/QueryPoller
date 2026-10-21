# Query Poller for SSMS

An SSMS 22 extension that re-runs a T-SQL query every *N* seconds and shows the latest results in a dockable tool window — handy for watching blocking, job progress, queue depth, replication latency, etc.

## Features

- **Tools → Query Poller** (also under **View → Other Windows**) opens a dockable tool window.
- **Connection from:** pick any connected query window. The poller opens its *own* connection with the same server, login, auth method (Windows, SQL, all Microsoft Entra ID modes), encryption and advanced options — your query window's session is never touched.
- **Copy query from window** copies the selected text (or the whole script if nothing is selected).
- Interval in seconds, command timeout, optional **Stop on error**, **Start/Stop** and **Run now**.
- Polls never overlap; the interval is measured start-to-start.
- Scripts can contain `GO` / `GO n` batch separators and return multiple result sets (pick one from the *Result set* dropdown).
- Sort order and scroll position are kept between refreshes when the result shape doesn't change.
- Status line shows run number, time, duration, row counts, rows affected and errors; the tab caption shows the server and interval while polling.
- Each result set is capped at 10,000 rows.
- Closing the tool window stops polling. Query, interval, timeout and stop-on-error are remembered.
- Poller sessions show up as `SSMS Query Poller` in `program_name`, so they're easy to spot (or exclude) in `sys.dm_exec_sessions`.

## Usage

1. In SSMS, open a query window and connect it to a server.
2. Open the poller from **Tools → Query Poller** or **View → Other Windows → Query Poller**. It's a normal tool window, so you can dock it anywhere (e.g. as a tab next to your query window).
3. In **Connection from:**, pick the connected query window whose connection you want to use. The active window is selected automatically when the poller first opens.
4. Type a query, or click **Copy query from window**. Optionally change the database.
5. Set the interval (seconds) and press **Start**. Press **Stop** to stop, or **Run now** to skip the remaining wait.

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

To uninstall, use **Extensions → Manage Extensions** in SSMS, or:

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

- The database box is pre-filled from the query window's connection settings (its initial database). If you switched databases in that window with `USE` or the toolbar dropdown, set the database here explicitly.
- `GO` detection is line-based (like `sqlcmd`), so a line containing only `GO` inside a block comment or string literal is still treated as a separator.
- `PRINT` / informational messages aren't shown; errors are shown in the status line.
