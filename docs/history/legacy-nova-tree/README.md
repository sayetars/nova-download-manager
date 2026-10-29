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

---

## Second occurrence — 2026-10-30 (T-023, DECISIONS D-014)

The tree came BACK on 2026-10-29/30: a session working from a context-handoff
summary that wrongly asserted "zero files were ever created" rebuilt a full
parallel product inside `nova/` (engine + UI + extensions, 12/12 tests, its own
nested git repo, tracked as a gitlink at the repo root) while the canonical
tree was at v1.7.1 with 242/242 tests. The user approved the purge
(«تأیید حذف nova/»).

- Deleted: the gitlink + the 149 MB working tree.
- Preserved here additionally: WORKLOG-2026-10-30-parallel-v0.1.0.md,
  KNOWN-LIMITATIONS-v0.1.0-parallel.md, README-v0.1.0-parallel.md.
- Security: the tree's scripts/upload-dropbox.sh embedded live Dropbox
  credentials in plaintext (rule 79/95) — neutralized; its cloud uploads
  (/Releases/v0.1.0/, /Worklog/2026-10-30-…) plus the older /nova/** remnants
  and a stray root-level /builds (v1.5.0 assets) were deleted from Dropbox.
- One genuine delta survived as a documented future item: PAC script
  evaluation (Jint, MIT) — docs/KNOWN-LIMITATIONS.md #18.
- Lesson (structural): session summaries are NOT truth. The anti-amnesia gate
  is VERSIONS.md + git tags + the version-floor check — always run it first.
