# Definition of done

Run, in order, and paste real output — no success claims without it:

1. `dotnet build Halo.sln -c Release` → **0 warnings, 0 errors** (the bar the user holds).
2. `dotnet test tests\Halo.Tests\Halo.Tests.csproj` → all green. Report the count (it has grown
   56 → 68 → 71…); a dropped count means a test was lost.
3. **Visual/behavioural proof for anything UI**: the matching `--render-*` dev hook PNG (see
   `mem:dev_hooks`) — never a screenshot of the running pill, it's capture-excluded and will show the
   window behind it. Say what the render showed.
4. If deployed: publish + install per `mem:suggested_commands`, relaunch, confirm no
   `%TEMP%\halo-crash.log` and the pill is alive.
5. Append a dated entry to `PROGRESS.md`: root cause, change, how it was verified, and explicitly
   **deployed vs pushed** state (these diverge constantly in this project — live hot-swapped dlls are
   routinely ahead of git).
6. Only then consider commit/push. Pushing to the public fork is a separate, comment-stripped
   pipeline — `mem:shipping`.
