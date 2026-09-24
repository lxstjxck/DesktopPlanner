# AGENTS.md

## Project

DesktopPlanner is a Windows desktop planner made of native desktop widgets and a local calendar.

The application is intentionally local-first. Tasks, notes, calendar events, inbox items,
completed tasks, window positions, and related state are stored locally. External calendar
synchronization is not part of the current product unless explicitly requested.

## Stack

- .NET SDK 10.0.401
- C#
- WPF on `net10.0-windows`
- MVVM with CommunityToolkit.Mvvm
- Microsoft.Extensions.DependencyInjection
- Entity Framework Core 10
- SQLite
- Ical.Net
- Serilog file logging
- WinForms `NotifyIcon` for tray integration
- Windows shell/native interop for desktop widget placement
- Inno Setup for the installer

Warnings are treated as errors.

## Solution structure

Dependency direction must remain:

```text
DesktopPlanner.Domain
        ↑
DesktopPlanner.Application
        ↑
DesktopPlanner.Infrastructure

DesktopPlanner.App
    → Application
    → Infrastructure
```

Responsibilities:

- `src/DesktopPlanner.Domain/` — core domain models/rules; no UI or infrastructure dependencies.
- `src/DesktopPlanner.Application/` — use cases, application services, interfaces; depends on Domain.
- `src/DesktopPlanner.Infrastructure/` — SQLite/EF Core, migrations, logging, calendar codec, Windows integration.
- `src/DesktopPlanner.App/` — WPF views, view models, application startup, tray UI and composition root.
- `tests/` — Domain, Application and Infrastructure test projects.
- `scripts/Smoke.ps1` — application smoke test.
- `scripts/Build-Installer.ps1` — release installer build.
- `installer/DesktopPlanner.iss` — Inno Setup definition.

Do not reverse these dependencies to make a quick fix.

## Core product invariants

1. Widgets must remain desktop widgets rather than ordinary always-on-top application windows.
2. Preserve the existing Windows shell/WorkerW desktop integration unless a task explicitly redesigns it.
3. Do not casually change `WindowsOverlayService*`; Windows shell behavior is fragile and must be tested on Windows.
4. Existing user data must survive normal application upgrades.
5. All planner data remains local unless network synchronization is explicitly requested.
6. Do not delete or reset a user's database as a migration strategy.
7. Window/widget positions, sizes, visibility and interaction state must not be reset by unrelated changes.
8. Keep tray behavior, startup behavior, shutdown behavior and single-application lifecycle stable.
9. UI must remain responsive; do not block the WPF UI thread with database or long-running work.
10. Do not change application version numbers or installer metadata unless the task is a release/version task.

## MVVM and UI rules

- Prefer bindings, commands and view models for UI state and user actions.
- Keep business logic out of XAML code-behind.
- Code-behind is acceptable for WPF-specific behavior that is inherently tied to the view, input, window handles, drag/drop, focus, or rendering.
- Put reusable application behavior in Application services rather than view classes.
- Put persistence and OS-specific implementation in Infrastructure.
- Keep XAML changes targeted; do not reformat large resource dictionaries during unrelated work.
- Reuse existing resources/styles before adding near-duplicates.
- Respect WPF dispatcher/thread-affinity requirements.

## Windows desktop integration

Before changing desktop/window behavior, inspect the relevant `WindowsOverlayService*.cs` files and the calling code.

Changes affecting any of the following require a Windows smoke/manual check:

- WorkerW/Progman attachment;
- desktop z-order;
- Explorer restart/recovery;
- virtual desktops;
- desktop click/input behavior;
- fullscreen behavior;
- widget activation/focus;
- multi-monitor geometry;
- DPI/scaling;
- tray restore/show/hide behavior.

Do not replace working shell integration with `Topmost=true` as a shortcut.

## Data and migrations

- EF Core migrations live in `src/DesktopPlanner.Infrastructure/Migrations`.
- Never edit an already-shipped migration merely to match a new model.
- For schema changes, create a new migration and verify upgrade of an existing database path.
- Keep migrations forward-safe and preserve existing rows whenever possible.
- Do not store machine-specific absolute paths in persisted portable domain data unless required.

Typical migration command:

```powershell
dotnet tool restore
dotnet ef migrations add <MigrationName> `
  --project src/DesktopPlanner.Infrastructure `
  --startup-project src/DesktopPlanner.App
```

Only create a migration when the persisted schema actually changes.

## Logging and local data

Runtime logs are written under the DesktopPlanner local application data directory.

- Do not log private task/note contents unless required for a narrowly scoped diagnostic.
- Never log secrets or sensitive machine data unnecessarily.
- Respect `DESKTOPPLANNER_DATA_DIR` when tests/smoke tooling overrides the data directory.
- Automated tests must not touch a real user's database or logs.

## Working method

For each task:

1. Identify the owning layer before editing.
2. Read the smallest relevant set of files.
3. Preserve the existing dependency direction.
4. Implement the smallest coherent change.
5. Add/update tests in the matching test project.
6. Build and run focused tests.
7. Run the full solution tests when practical.
8. Run the smoke script for changes affecting startup, persistence, windows, widgets or UI behavior.
9. Inspect the final diff for unrelated XAML formatting, generated output, database files, logs and version changes.

Avoid repository-wide refactors during a small feature or bug fix.

## Standard commands

Restore:

```powershell
dotnet restore DesktopPlanner.sln
dotnet tool restore
```

Build:

```powershell
dotnet build DesktopPlanner.sln --no-restore
```

Tests:

```powershell
dotnet test DesktopPlanner.sln --no-build
```

Smoke test on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Smoke.ps1
```

For relevant desktop/fullscreen/input changes, use the existing smoke flags where applicable:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Smoke.ps1 -Fullscreen
powershell -ExecutionPolicy Bypass -File scripts/Smoke.ps1 -DesktopInput
```

Build the installer only for release/installer work:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-Installer.ps1
```

## Definition of done

A change is done when:

- the solution builds with zero warnings/errors;
- relevant tests pass;
- persisted-data compatibility is preserved;
- MVVM/layer boundaries remain intact;
- no real user data is touched by tests;
- Windows desktop integration is smoke/manual checked when affected;
- installer/version files are unchanged unless intentionally part of the task.

## Response style

After implementation, report only:

- what changed;
- important files changed;
- tests/build/smoke checks run and their result;
- any remaining Windows-specific manual check that is genuinely required.

Do not provide long tutorials unless explicitly asked.
