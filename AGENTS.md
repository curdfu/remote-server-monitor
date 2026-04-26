# AGENTS.md

This file gives coding agents repository-specific guidance for working in
`remote-server-monitor`.

## Project Overview

- Backend: .NET 8 solution centered on `src/Monitor.Service`.
- Frontend: Vue 3 + TypeScript + Vite app in `src/Monitor.Frontend`.
- Storage: SQLite initialized in-process by `Monitor.Storage`.
- Runtime target: Windows 11 / Windows Server; some hardware/network code is
  intentionally Windows-only.
- Entry solution: `win11-system-monitor.sln`.

## Repository Layout

- `src/Monitor.Service`: host process, startup, hosted services, Windows service
  integration.
- `src/Monitor.WebApi`: minimal API endpoints, SignalR hub, exception middleware.
- `src/Monitor.Storage`: SQLite access, initialization, persistence, retention.
- `src/Monitor.Network`: ETW-based network collection and aggregation.
- `src/Monitor.Hardware`: LibreHardwareMonitor-based hardware collection.
- `src/Monitor.Contracts`: shared DTOs, options, enums, interfaces.
- `src/Monitor.Frontend`: Vue UI, API client, SignalR client, CSS.
- `scripts`: publish, deploy, install, cleanup, and ETW verification helpers.
- `artifacts`: build/publish output.

## Toolchain And Environment

- .NET SDK: 8.x (`TargetFramework` is `net8.0`).
- Node/Vite frontend with `npm` and `package-lock.json`.
- Solution-wide defaults come from `Directory.Build.props`:
  - `Nullable` enabled.
  - `ImplicitUsings` enabled.
  - `LangVersion` set to `latest`.
  - `TreatWarningsAsErrors` is `false`.
- Central NuGet package versions are in `Directory.Packages.props`.

## Build Commands

- Build the full .NET solution:
  - `dotnet build win11-system-monitor.sln`
- Build the frontend:
  - `npm run build` (run in `src/Monitor.Frontend`)
- Build and publish the Windows service bundle:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\publish-service.ps1`
- Build and deploy to a target directory:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\publish-and-deploy.ps1 -DeployDir 'C:\path\to\deploy'`
- Publish frontend assets only:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\publish-frontend-only.ps1`

## Run Commands

- Run the backend locally:
  - `dotnet run --project src/Monitor.Service/Monitor.Service.csproj`
- Run the frontend dev server:
  - `npm run dev` (run in `src/Monitor.Frontend`)
- Default local frontend port from `vite.config.ts`:
  - `5173`
- Default backend listen settings from `src/Monitor.Service/appsettings.json`:
  - HTTP port `5188`
  - listen address `127.0.0.1`

## Lint / Formatting / Validation

- There is no dedicated lint script, ESLint config, Prettier config, or test
  runner config checked in today.
- Practical validation commands currently are:
  - `dotnet build win11-system-monitor.sln`
  - `npm run build` in `src/Monitor.Frontend`
- If you need to sanity-check ETW/network behavior, use:
  - `powershell -ExecutionPolicy Bypass -File .\scripts\verify-etw-network.ps1`
- Do not assume `dotnet format`, ESLint, Vitest, or Jest are configured unless
  you add that tooling as part of your change.

## Test Commands

- There are no test projects or frontend test suites in the repository today.
- `dotnet test win11-system-monitor.sln` currently has nothing meaningful to
  execute.
- If you add tests, document the new commands in this file.

## Single-Test Guidance

- Current status: no single-test command exists because no tests are present.
- If a .NET test project is added later, the expected single-test pattern is:
  - `dotnet test path\to\Project.Tests.csproj --filter "FullyQualifiedName~TestName"`
- If a JS/Vitest suite is added later, the expected single-test pattern is
  usually one of:
  - `npm test -- --runInBand test-file-name`
  - `npx vitest run path/to/test-file.spec.ts -t "test name"`
- Do not claim a single-test command works unless you verified the project
  actually contains a test runner.

## Existing Repo Rules

- No `.cursorrules` file is present.
- No `.cursor/rules/` directory is present.
- No `.github/copilot-instructions.md` file is present.
- Therefore, repository-specific agent guidance currently comes from this file,
  the checked-in code, and existing scripts/configuration.

## C# Style Guidelines

- Prefer file-scoped namespaces.
- Keep `using` directives at the top of the file.
- Put `System.*` usings first, then framework/package usings, then project
  usings.
- The codebase frequently uses primary constructors for services and middleware;
  follow that pattern when it keeps the type concise.
- Use `sealed` for concrete service/helper classes unless inheritance is needed.
- Use PascalCase for public types, methods, and properties.
- Use `_camelCase` for private readonly fields.
- Keep local variables in `camelCase`.
- Use `var` when the type is obvious from the right-hand side; otherwise prefer
  an explicit type when it improves readability.
- DTOs and immutable response models commonly use `init` properties.
- Favor small extension methods for registration and startup composition.
- Keep minimal API mapping code in `Monitor.WebApi`, not in `Program.cs`.
- Prefer async I/O end-to-end and thread cancellation through
  `CancellationToken` parameters.
- Guard public methods with argument validation where needed, for example
  `ArgumentNullException.ThrowIfNull(...)`.
- Use raw string literals for multi-line SQL.
- Keep SQL parameterized; follow the existing `$parameterName` SQLite style.
- Prefer `DateTimeOffset.UtcNow` and ISO-8601 (`"O"`) timestamps.
- Respect nullable reference types; avoid introducing null-forgiving operators
  unless unavoidable.
- Do not disable warnings casually; fix the nullability or platform issue when
  practical.

## C# Error Handling And Logging

- Use structured logging with message templates; do not interpolate log strings.
- Log with context-rich property values, as seen in startup and settings
  persistence code.
- Catch exceptions only when you can add context, degrade gracefully, or convert
  to a stable API response.
- Let the global exception middleware own unhandled HTTP 500 responses.
- For recoverable background-processing issues, log and continue instead of
  crashing the process.
- Preserve Windows-specific behavior; platform warnings in hardware code are
  expected and should not be "fixed" by breaking Windows functionality.

## Frontend Style Guidelines

- Use TypeScript `strict` mode assumptions; avoid `any`.
- Use ES module syntax and keep imports grouped logically.
- Prefer `import type` for type-only imports.
- Use single quotes and semicolons, matching the existing frontend files.
- Keep indentation at 2 spaces in TS, Vue, and JSON-style config.
- Use `script setup` for Vue SFCs.
- Component, view, and DTO type names use PascalCase.
- Functions, refs, computed values, and locals use camelCase.
- Keep route names lowercase strings such as `dashboard`, `network`, `settings`.
- Centralize HTTP calls in `src/Monitor.Frontend/src/services/api.ts`.
- Centralize SignalR connection behavior in
  `src/Monitor.Frontend/src/services/realtime.ts`.
- Reuse shared DTO/type definitions from `src/Monitor.Frontend/src/types`.
- Prefer computed properties and small helpers over dense template logic.
- Preserve the existing CSS-variable-driven design system in `src/Monitor.Frontend/src/styles.css`.
- Do not introduce a new UI library unless the task explicitly requires it.

## Naming And Architecture Conventions

- Keep assemblies aligned to responsibility boundaries already present in `src/`.
- Contracts belong in `Monitor.Contracts`; avoid duplicating DTOs in feature
  projects.
- Storage concerns stay in `Monitor.Storage`; Web API should call abstractions or
  repositories/services, not inline SQL.
- Background loops, polling, and periodic work belong in hosted services or
  dedicated service classes.
- Extension methods named `Add...` and `Map...` are the preferred composition
  pattern for DI and endpoint registration.

## Agent Working Agreement

- Before changing code, inspect adjacent files and match the local style.
- Keep changes focused; do not refactor unrelated areas opportunistically.
- Validate with the smallest relevant commands first, then broader builds if the
  change crosses backend/frontend boundaries.
- If you add tooling, tests, lint rules, or new workflow commands, update this
  file in the same change.
