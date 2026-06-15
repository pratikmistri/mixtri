# Copilot Agent Instructions

## Learnings File

At the **start** of every task, read `learnings.md` (in the repository root) to understand what approaches have already been tried, what worked, and what didn't. Use this context to avoid repeating failed approaches and to build on proven solutions.

At the **end** of every task, update `learnings.md` with:

- **Feature/area**: What part of the codebase was changed.
- **Approaches tried**: Each approach attempted during the task.
- **What worked**: The approach that succeeded and why.
- **What didn't work**: Approaches that failed and why, so they aren't retried.

Keep entries concise. Append new entries — never remove or overwrite existing ones.

## Build and Launch After Every Change

After every feature or fix change, **always build the app and launch it** so the user can verify the change live. Do not call `task_complete` until both have succeeded.

Steps:

1. **Kill any running instance** (the build will otherwise fail with file-lock errors):

   ```powershell
   Get-Process | Where-Object { $_.ProcessName -eq 'Musio.App' } | ForEach-Object { Stop-Process -Id $_.Id -Force }
   ```

   Session policy rejects `Stop-Process -Id $var` and `Stop-Process -Name` — must use the pipeline + `ForEach-Object` form above.

2. **Build with Visual Studio MSBuild** (NOT `dotnet build` — it fails with a missing PriGen task on this repo). Wrap in `[System.Diagnostics.Process]::Start()` because direct `& MSBuild.exe` invocation is blocked:

   ```powershell
   $msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
   # Args: src\Musio.App\Musio.App.csproj /restore /t:Build /p:Configuration=Debug /p:Platform=x64 /v:minimal /nologo
   ```

3. **Launch the built exe** so the user can verify:

   ```powershell
   Start-Process -FilePath "C:\Users\prmistri\source\repos\musio\src\Musio.App\bin\x64\Debug\net9.0-windows10.0.26100.0\win-x64\Musio.App.exe"
   ```

4. Confirm the process is running (`Get-Process -Name Musio.App`) before reporting the task complete.
