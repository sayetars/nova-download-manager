# RESUME-POINT — atomic continuation contract

updated: 2026-10-31 (T-026 — Windows CI bring-up COMPLETE: full-green run + live GitHub Release v1.8.0)
task: NONE OPEN — T-026 shipped; awaiting real-Windows install feedback
substep: —

done (T-026, this session — 13 evidence-driven CI rounds):
- Pushed the repo (T-025 lane) → first windows-build.yml run burnt 90 min
  silently in the test suite (37 failures + 80-min hang).
- Diagnosis ladder (not guessing): test Wait helpers dump engine diagnostics
  on timeout; NOVA_TEST_TIMEOUT_SCALE (CI=3); --blame-hang 20m in the
  workflow; loopback test HttpClients UseProxy=false + ConnectTimeout 15s.
- Five latent Windows-only PRODUCT bugs found + fixed (all invisible on
  Linux — see D-017): finalize-with-open-handle (every HTTP download looped
  forever on Windows), hash/PGP verifier share-conflict with the live
  writer, pipe DACL ReadWrite→FullControl (engine served ONE connection per
  pipe on Windows), IpcConnection pipe-instance leak, ephemeral PEM client
  keys (SChannel) → PFX round-trip. Plus YtDlpBridge.Quote always-quotes on
  Windows (cmd.exe splits unquoted args on = ; ,).
- CI/packaging repairs: Windows .cmd fake binaries (SHIFT-loop parsing — a
  ")" in ".%(ext)s" breaks FOR-sets; 8-char suffix), test certs get
  serverAuth/clientAuth EKUs, ClientCert mTLS handshake documented-skip on
  Windows (SChannel untrusted-chain rule), Flutter runner C++ fixed against
  the real 3.47.7 headers (GetInstance/Success; local _DROPFILES replica +
  minimal IEnumFORMATETC), dependency-free STORE zip writer for extensions,
  Farsi.isl vendored (official jrsoftware translation), installer payload
  layout assembled by CI (build-release.ps1 contract), iss+ps1 zip names
  wildcarded, installer [Code] per official AnsiString signatures (+Firefox
  gets its own allowed_extensions manifest), 'BeforeInstall: none' dropped.
- GREEN: run 41146761713 — every step (audit, engine build, **318/318
  engine tests on real Windows**, Flutter Windows UI compiled, extensions,
  Inno Setup installer, artifacts uploaded).
- RELEASE: repo sayetars/nova-download-manager (created via the GitHub App);
  v1.8.0 tag moved to the fixed commit b826a5a; tag run 41147670500 green;
  GitHub Release LIVE: github.com/sayetars/nova-download-manager/releases/
  tag/v1.8.0 with **nova-setup-1.8.0.exe (39.4 MB)** attached.
- Memory docs: worklog T-026, D-017, STATE, VERSIONS (builds table:
  nova-setup-1.8.0.exe + the Dropbox engine-zip supersession note),
  KNOWN-LIMITATIONS (Windows-UI row closed).

next:
1. USER: install nova-setup-1.8.0.exe on a real Windows machine → feedback
   (install flow, tray, drag-out, extension registration, a real download).
   Bugs found → v1.8.x PATCH lane: bump version.json → sync → test →
   commit → `python3 scripts/github_release_push.py push` → move/push the
   tag → CI builds + releases automatically.
2. Repo maintenance: scripts/github_release_push.py plan|push|status
   --watch; scripts/github_app_probe.py validates the App lane; local-only
   credentials live in tools/github/ (600, gitignored, never archived).
   NOTE: the sandbox recycles tools/github/* (key + creds) and the
   toolchains — rebuild per RECOVERY.md §2 and re-copy the key from
   upload/ when needed.
3. Optional follow-ups (all free): upload the CI's fixed portable engine
   bundle to Dropbox builds/ under a clearly-marked name (the 2026-10-30
   Dropbox v1.8.0 engine zip predates the Windows fixes — superseded);
   Linux tray + Linux drag-out; embedded player; CWS/AMO store publishing.

evidence:
- CI logs across rounds (io_error sharing violations with received==total;
  "Access to the path is denied" instance-create storms; MSVC
  DROPFILES/StandardMethodCodec errors; iscc AnsiString mismatch); green
  run 41146761713; tag run 41147670500; releases API listing (v1.8.0,
  asset nova-setup-1.8.0.exe 39.4 MB); local dotnet test 318/318.

blockers: none — Windows CI + release lane fully operational.

recovery: RECOVERY.md
