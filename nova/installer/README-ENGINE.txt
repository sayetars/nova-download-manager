Nova Download Engine v0.1.0 (win-x64, self-contained .NET 8)
================================================================

Run from cmd or PowerShell:

  NovaEngine.exe                       help
  NovaEngine.exe download <url>        download a file
      --out <path>                     target file path
      --segments <n>                   parallel connections (1-32)
  NovaEngine.exe serve                 start the IPC service
  NovaEngine.exe list                  list tasks from the local database
  NovaEngine.exe pause <id> | resume <id> | cancel <id>   (needs 'serve')

Data:
  Database:        %USERPROFILE%\.nova\nova.db  (SQLite, WAL)
  Default folder:  %USERPROFILE%\Downloads

Notes:
  - Multi-segment (HTTP Range) downloads, pause/resume, crash recovery
    (interrupted tasks auto-resume on next 'serve' start), retry with
    exponential backoff, ETag/size validation on resume.
  - IPC protocol v1 (JSON lines) over Named Pipe \\.\pipe\nova-engine-v1
    - for the Flutter UI / browser extension host.
  - This is the engine component of Nova Download Manager; the Flutter UI
    is built by the repository's GitHub Actions workflow or build-windows.ps1.
