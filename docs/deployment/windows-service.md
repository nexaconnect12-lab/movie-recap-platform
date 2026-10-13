# Windows Service deployment
Worker uses Microsoft.Extensions.Hosting.WindowsServices / AddWindowsService and runs as a console app during development. Service installation is an administrative action; scripts are supplied, not automatically invoked.

From repository root, publish:

```powershell
dotnet publish src/Workers/ProcessingWorker -c Release -r win-x64 --self-contained true -o artifacts/worker
```

In elevated PowerShell, configure an appropriate database runtime credential, then:

```powershell
./scripts/Install-Service.ps1 -Executable (Resolve-Path artifacts/worker/MovieRecap.Worker.exe).Path -ConnectionString $env:ConnectionStrings__Platform
./scripts/Start-Service.ps1
./scripts/Stop-Service.ps1
./scripts/Uninstall-Service.ps1
```

Default identity is the dedicated virtual account NT SERVICE\MovieRecap.Processing. Installer grants executable directory read/execute and log directory modify permission. Default logs: ProgramData\MovieRecap\Logs. Automatic start and three increasing restart delays are configured. Verify ACLs, protect binaries from modification by the service identity, provision input/output folder access in Phase 2, and restrict network database access. Do not use an administrator identity.

Service-specific connection environment is stored under its service registry key, restricted to SYSTEM/Administrators. It is not an encrypted vault and local administrators can read it. Use integrated/managed credentials or a dedicated protected secret-provider integration for production. No OAuth/provider tokens are installed.

Shutdown stops polling, cancels active execution and heartbeat, and leaves an interrupted lease to expire and retry safely. Process crashes use the same recovery path. Current executor only performs a database check, so recovery has no irreversible side effect. Configure graceful shutdown timeout for future long-running executors. Unexpected infrastructure poll failures are logged and retried. Fatal host failures can trigger SCM recovery.

Readiness is reported by API; job state, WorkerId, lease expiry and audit expose processing status. Separate worker heartbeat/metrics endpoint and alerting remain hardening work. Scripts were syntax-reviewed and publish output verified; SCM installation/start/recovery requires a deployment test on an administrative Windows host.


For scene jobs, install the pinned Python environment explicitly for the service deployment and set Scenes__PythonPath to its executable, accessible to the worker identity. Publish includes Scenes/scene_runner.py and requirements.txt. Apply VideoSceneAnalysis with the migration role before restarting upgraded hosts. Confirm configured trusted-root read permissions and interpreter execution permissions; no service installation/SCM acceptance was performed in this scene slice. Resource limits and error behavior: ../api/scene-analysis.md.

Selected-audio artifacts require a trusted separate Audio__Root visible to API/worker accounts and an actual worker-accessible Audio__FfmpegPath. Apply VideoAudioExtraction before restarting upgraded hosts. Preserve both artifact tree and database in backup/restore; prevent external modification of managed files. No system tool/service installation or administrative SCM acceptance was performed. See ../api/audio-extraction.md for bounds/cleanup/limits.

For scene PNGs, apply VideoFrameSampling before restarting upgraded hosts and configure Frames__Root identically for API/worker plus actual worker-accessible Frames__FfmpegPath. Root permissions must allow the required ownership/lock/staging/read operations; preserve complete batch trees with database backups. No administrative SCM/service install acceptance was performed. See ../api/scene-frames.md.
