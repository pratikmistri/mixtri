# Copilot Agent Instructions

## Learnings Files

At the **start** of every task, read `learnings/playbooks.md` (small, definitive, always) to
understand the settled rules for this repo. These are distilled from areas that caused repeated
churn and should not be re-litigated without new evidence.

Consult the archives — `learnings/archive-2026-H1.md` and `learnings/archive-2026-H2.md` — **only**
when your task touches one of these specific high-churn areas, since that is where the detailed
round-by-round history lives:

- DPI / capture coordinate transforms
- Compositor / frame-style
- Zoom-segment time mapping
- Export / H.264 encoding
- Text slides / transitions
- Preview lifecycle

Otherwise, skip the archives — they exist for historical context, not routine reading. See
`learnings.md` at the repo root for the full index of what lives where.

At the **end** of every task, append your entry to the **current archive file**
(`learnings/archive-2026-H2.md`), not to the root index, with:

- **Feature/area**: What part of the codebase was changed.
- **Approaches tried**: Each approach attempted during the task.
- **What worked**: The approach that succeeded and why.
- **What didn't work**: Approaches that failed and why, so they aren't retried.

Keep entries concise. Append new entries — never remove or overwrite existing ones.

Promote a rule into `learnings/playbooks.md` only once it has caused repeat churn (multiple rounds
of rework, reversals, or regressions) — playbooks stay small deliberately.
