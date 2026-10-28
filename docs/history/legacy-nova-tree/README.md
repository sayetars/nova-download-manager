# Legacy `nova/` tree — historical archive (2026-10-29, T-021)

This directory preserves ONLY the narrative files from the abandoned parallel
implementation tree that lived at `nova/` (2026-10-28/29, "v0.1.0 vertical
slice" era). The full legacy tree (73 MB of code, its own engine/app/
extensions/installer subtrees) was REMOVED from the repository in T-021
because:

1. Zero references from the canonical tree (src/, apps/, extensions/, tests/,
   scripts/, docs/ — grep-verified).
2. It described a PARALLEL, much less advanced implementation (engine v0.1.0,
   "Flutter scaffold only") while the canonical tree was at v1.6.0 with 208+
   tests — anyone landing in the repo could not tell which tree was real.
3. It was accidentally committed under a UUID message (3c219fd) and shipped
   inside every cloud source checkpoint (~duplicate payload in each backup).

This is the "versions must not get mixed" directive made structural:
ONE tree (the canonical root), ONE history (worklog.md), ONE version
(version.json). The files below keep the legacy narrative for archaeology.

- WORKLOG.md — legacy master index (round 12 end-state)
- SESSIONS.md — session notes incl. research rounds 4
- ROUND-12.md — Windows-build research + engine v0.1 slice
- KNOWN-LIMITATIONS.md — legacy-era limitations (superseded by docs/KNOWN-LIMITATIONS.md)

The authoritative current history is /worklog.md + /VERSIONS.md + /STATE.md.
