# Copilot Agent Instructions

WinUI 3 / .NET 9 screen-recording and video editor. `src/Mixtri.App` (UI), `src/Mixtri.Core`
(logic, no UI deps), `src/Mixtri.Tests` (MSTest against Core).

## Build & test

`dotnet build` and plain `dotnet test` both fail here with a PriGen `MSB4062` error. Use VS
MSBuild (find it with `vswhere -latest -find 'MSBuild\**\Bin\MSBuild.exe'`), then test `--no-build`:

```
msbuild src\Mixtri.Tests\Mixtri.Tests.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64
dotnet test src\Mixtri.Tests\Mixtri.Tests.csproj --no-build -c Debug -p:Platform=x64
```

Set `DOTNET_ROLL_FORWARD=Major` if the .NET 9 runtime is missing. The full suite must stay
green. It uses disposable fixtures and has no production access: run it, fix
failures your change caused, and rerun without asking for approval at each step.

App build, MSIX packaging, Store version bumps, and local deploy/launch are in
`learnings/playbooks.md` → "Build, Deploy & Test toolchain". Read that section before packaging or
deploying — the version-bump and PFN rules there are not guessable.

## Settled rules

`learnings/playbooks.md` holds short, definitive rules for areas that caused repeated churn. Read
the section matching your task rather than the whole file:

| Touching | Read |
| --- | --- |
| DPI, region/window/monitor capture coordinates | "DPI & capture coordinate transforms" |
| Export, encoding, `MediaEncodingProfile` | "H.264 encoding & MediaEncodingProfile" |
| Flyouts, XAML resources, control init | "WinUI flyout/control initialization" |
| Drag gestures revealing transient UI | "Transient UI revealed by a drag gesture" |
| Multi-recording zoom segments | "Zoom-segment time mapping" |
| Crash, freeze, device-loss, Win2D | "Crash / freeze hardening invariants" |

These supersede conflicting advice elsewhere; don't re-litigate them without new evidence.

`learnings/archive-2026-H1.md` and `-H2.md` are the round-by-round history (large). Open one only
when a playbook rule is unclear or you're debugging a recurring failure in DPI/capture,
compositor/frame-style, zoom-segment mapping, export/H.264, text slides/transitions, or preview
lifecycle. Skip them otherwise.

## Definition of done

A code change is done when it builds, the affected tests pass, and you've verified the behavior you
changed — not at the first implementation. If a fix reveals an adjacent break that your change
caused, fix that too rather than stopping to report it.

## Recording learnings

After a task that changed code or behavior, append one concise entry to
`learnings/archive-2026-H2.md` (the current archive, not `learnings.md`): feature/area, approaches
tried, what worked and why, what didn't and why. Append only — never remove or reword existing
entries. Skip this for questions, reads, and trivial edits.

Promote a rule into `learnings/playbooks.md` only after it has caused repeat churn (multiple rounds
of rework, reversals, or regressions) — playbooks stay small deliberately.
