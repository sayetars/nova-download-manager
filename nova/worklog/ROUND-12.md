# Round 12 — 2026-10-29 — Windows build path research + Engine v0.1

## The question
"Is there a way to produce a **Windows build** from this Linux environment and hand it to the user?"

## Research findings

1. **Flutter Windows UI cannot be built on Linux.** `flutter build windows` requires a Windows host with
   the Visual Studio 2022 C++ toolchain (MSVC + Windows SDK). Cross-building from Linux/macOS has been a
   long-standing community request and is still unsupported. Community hacks (Wine + MSVC, custom
   clang-cl toolchains) are not production-grade — rejected.
2. **.NET 8 cross-compiles to Windows from Linux — officially supported.**
   `dotnet publish -r win-x64 --self-contained` produces a real Windows executable, including native
   libraries (SQLite `e_sqlite3.dll` ships inside the Microsoft.Data.Sqlite package per-RID).
   **Verified in practice this round.**
3. **GitHub Actions `windows-latest` hosted runners** solve the Flutter half for free:
   a complete workflow is now in `.github/workflows/windows-build.yml`
   (engine publish + `flutter create --platforms=windows` if the runner folder is absent +
   `flutter build windows --release` + portable zip artifact + optional tag release).
4. Docker Windows containers need a Windows kernel — rejected on Linux hosts.

## Implemented this round

**Nova.DownloadEngine v0.1.0** (single console binary, `NovaEngine`):
- HTTP probe (HEAD with ranged-GET fallback): size, Accept-Ranges, ETag, Last-Modified
- Multi-segment downloads (1–32 segments) using HTTP Range + `If-Range`
- Thread-safe positioned writes via `SafeFileHandle` + `RandomAccess.WriteAsync`
- Pause / resume / cancel; retries with exponential backoff (5 attempts, ≤60 s)
- Crash recovery: segment checkpoints persisted to SQLite (WAL) every ~2 s;
  interrupted tasks auto-resume (with remote validation) at next `serve` startup
- Versioned JSON-line IPC (protocol v1): Named Pipes on Windows, Unix sockets on Linux
- CLI: `serve | download | list | pause | resume | cancel`
- Fixed during testing: `Pausing -> Paused` terminal-state transition bug

## Self-tests (Linux, this environment)

| Test | What | Result |
|---|---|---|
| 1 | 4-segment download of 8 MiB random data from a range-capable local server; sha256 compare | **PASS — byte-identical** |
| 2 | IPC flow: ping → add (4 segments) → pause @ 81,920 B → resume → Completed; sha256 compare | **PASS — byte-identical** |

Engine log excerpt (test 2): `worker started (4 segments, 0 already)` → `stop requested -> Pausing`
→ `cancelled -> Paused @ 81920` → `worker started (4 segments, 81920 already)` → `Completed`.

## Deliverables

- `NovaEngine.exe` — win-x64, self-contained single file (cross-compiled from Linux in this round)
- `.github/workflows/windows-build.yml` — full-app cloud build (no Windows machine needed)
- `scripts/build-windows.ps1` — one-command local Windows build
- `docs/BUILD-FA.md` — Persian build instructions

## Known limitations (honest list)

- Windows-specific runtime paths (Named Pipe transport, app paths under `%USERPROFILE%`) are compiled
  but not executed on a real Windows machine in this round — no Windows host exists in this environment.
  The identical framing/protocol logic is exercised daily on Linux via UDS.
- Flutter UI is a scaffold; the full bilingual (EN/FA + RTL) interface is next.
- Extensions, video-detection overlay, and the full IDM gap list are not started yet.

## خلاصه فارسی
تحقیق انجام شد: بیلد UI فلاتر از لینوکس ممکن نیست (نیازمند MSVC روی ویندوز)، ولی موتور C# کراس‌کامپایل شد
و فایل `NovaEngine.exe` ویندوزی ساخته و تحویل شد. برای بیلد کامل (UI+موتور)، ورک‌فلوی GitHub Actions آماده است:
فقط ریپو را push کنید و ورک‌فلو را اجرا کنید. موتور v0.1 با تست‌های خودکار (دانلود چندسگمنتی + توقف/ادامه + IPC)
در همین محیط لینوکس تأیید شد.

## Next round

- Full Flutter UI (EN/FA, RTL) on top of the IPC client contract
- MV3 extension skeleton + native messaging host registration
- Scheduler/queue layer, speed limiter, video detection pipeline, yt-dlp bridge
