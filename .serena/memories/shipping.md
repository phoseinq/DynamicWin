# Shipping — sign, installer, release, public fork

Product ships as **DynamicWin** (`DynamicWinSetup.exe` + `DynamicWinPortable.zip`) on the
`phoseinq/DynamicWin` GitHub repo, even though the code/app is Halo. Bump `<Version>` in
`src/Halo.App/Halo.App.csproj` for a release.

## Sequence
1. Publish App + Hooks into `dist\app` (see `mem:suggested_commands`).
2. **Sign both inner exes** — self-signed cert thumbprint `2EB268F09FEA535E92FB395FA2FAB4409EC22E1D`
   in `CurrentUser\My`. `signtool sign /tr http://timestamp.digicert.com /td SHA256 /fd SHA256`;
   fall back to the sectigo timestamp server, then unsigned-time as a last resort.
3. `ISCC installer\Halo.iss` → `dist\DynamicWinSetup.exe`. **Retry up to 6×**: antivirus locks the output
   mid icon-embed and it fails with `EndUpdateResource failed (110)`. Sign the installer too.
4. Portable: copy `dist\app` → `dist\Halo`, `Compress-Archive` → `dist\DynamicWinPortable.zip`.
5. `gh release upload <tag> dist\DynamicWinSetup.exe dist\DynamicWinPortable.zip --repo phoseinq/DynamicWin`
   (delete the existing asset first to replace it).

## Public-fork push ("Boy" branch) — comments are stripped
The public fork does **not** carry this repo's comments. Pipeline: run the comment-strip tool at
`C:\Users\hosei\AppData\Local\Temp\halo_pr\strip` (`dotnet run -- <dir>`), copy the stripped `.cs` into the
fork clone at `C:\Users\hosei\AppData\Local\Temp\halo_pr\fork`, and commit **as phoseinq with NO
`Co-Authored-By` trailer**, then push. (Those temp paths are recreated when missing.)
So: local `master` = comment-bearing truth; the fork = stripped mirror. Never assume the fork's content
reflects local formatting.

## Autostart
Scheduled Task `Halo` at logon **plus** an `HKCU\...\Run\Halo` entry — the Run key is the fallback because
Fast Startup (`HiberbootEnabled=1`) skips the at-logon task when powering on from shutdown. Safe to have
both: the single-instance mutex dedupes.
