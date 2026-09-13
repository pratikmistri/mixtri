# Learnings

This file is now a short index. The content that used to live here has moved so that
agents do not have to load ~500 KB of history at the start of every task.

## Where things live

- **`learnings/playbooks.md`** — short, definitive rules distilled
  from areas that caused repeated churn (build toolchain, DPI/capture transforms, H.264
  encoding, WinUI flyout pitfalls, compositor coordinate model, zoom-segment time mapping,
  crash/freeze invariants). Read the section that matches your task.
- **`learnings/archive-2026-H1.md`** — older half of the chronological, per-fix history
  (consolidated entries through the single-file .mixtri project format work).
- **`learnings/archive-2026-H2.md`** — newer half of the chronological, per-fix history
  (MP4-backed-editing regressions through the most recent entry). New entries are
  appended to the END of this file.

## When to read which

- Read the `learnings/playbooks.md` section covering the area you're changing.
- Only open an archive file when a playbook rule is unclear or you're debugging a recurring
  failure in DPI/capture coordinates, compositor/frame-style, zoom-segment mapping,
  export/H.264, text slides/transitions, or preview lifecycle. Otherwise skip the archives.

## Adding new entries

At the end of a task, append a new entry to `learnings/archive-2026-H2.md` (the current
archive file) using the existing format: Feature/area, Approaches tried, What worked,
What did not work. Keep entries concise. Append only — never remove, reword, or reorder
existing entries. Promote a rule into `playbooks.md` only once it has caused repeat churn.

