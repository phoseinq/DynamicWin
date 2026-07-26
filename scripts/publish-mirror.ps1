<#
.SYNOPSIS
    Builds the public DynamicWin tree out of local master and publishes it, comments stripped.

.DESCRIPTION
    Local master is the comment-bearing truth; the public fork is a mechanically stripped mirror. That was
    a hand-run sequence of six steps, which is why it kept stalling a release mid-flight and why a pull
    request merged on the fork could be deleted by the next push.

    Both problems are structural, and this script fixes them structurally:

      * the public tree is DERIVED, never edited -- src/ and tests/ come from master, and every file that
        exists only on the public side (workflows, CODEOWNERS, the policy script, the public LICENSE and
        READMEs) lives in mirror/ here. master is therefore the single source of truth for the mirror, and
        nothing on the public side can be lost by regenerating it.

      * before it will commit, it diffs the tree it built against the published branch and REFUSES if any
        file would disappear, printing them. That is the guard that would have caught an outside patch
        being erased.

    Everything is assembled in a temp directory. The repository's own index is never touched.

.EXAMPLE
    pwsh scripts\publish-mirror.ps1 -Message "v3.1.0 - real Edge progress, working cancel, auto-update"
    pwsh scripts\publish-mirror.ps1 -Message "..." -Push
#>
param(
    [string]$Message,
    [string]$Branch = 'V3',
    [string]$Remote = 'origin',
    [switch]$Push,
    [switch]$SkipBuild,
    [switch]$AllowDirty,
    [switch]$AllowDeletions,
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'

# Everything master contributes to the public tree, as an allowlist. A denylist would publish the next
# private directory somebody adds; this cannot. LICENSE and the READMEs are deliberately absent -- the
# public ones differ (MIT there, and no private build instructions) and must come from mirror/.
$FromMaster = @('.gitignore', 'Halo.sln', 'ReadmeFiles', 'src', 'tests')

# mirror/ must supply these, or the publish is wrong in a way that is easy to miss. A missing LICENSE
# would quietly relicense the public repository on the next push.
$RequiredOverlay = @('LICENSE', 'README.md', 'README.fa.md', 'scripts/verify-public-source.ps1',
                     '.github/CODEOWNERS', '.github/workflows/ci.yml', '.github/workflows/codeql.yml')

$Identity = @{ Name = 'phoseinq'; Email = '129876188+phoseinq@users.noreply.github.com' }

function Fail([string]$text) { Write-Error $text; exit 1 }

# a plain function on purpose: declaring parameters would make this an advanced function, and then git's
# own short flags start binding to PowerShell common parameters (-o became an ambiguous -OutVariable)
function Invoke-Git {
    $out = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) { Fail ("git " + ($args -join ' ') + "`n" + ($out -join "`n")) }
    return $out
}

$repo = (& git rev-parse --show-toplevel 2>$null)
if (-not $repo) { Fail 'not inside a git repository' }
$repo = $repo -replace '/', [IO.Path]::DirectorySeparatorChar
$gitDir = Join-Path $repo '.git'

if (-not (Get-Command tar -ErrorAction SilentlyContinue)) { Fail 'tar is required (ships with Windows 11)' }

if (-not $AllowDirty) {
    $dirty = & git -C $repo status --porcelain -- $FromMaster
    if ($dirty) {
        Fail ("the mirror is built from committed HEAD, but these tracked paths are dirty:`n" +
              ($dirty -join "`n") + "`ncommit them, or pass -AllowDirty to publish HEAD anyway")
    }
}

if (-not $Message) { $Message = (& git -C $repo log -1 --format=%s HEAD) }
if (-not $Message) { Fail 'could not derive a commit message; pass -Message' }

Write-Host "fetching $Remote/$Branch" -ForegroundColor Cyan
Invoke-Git -C $repo fetch $Remote $Branch | Out-Null
$parent = (& git -C $repo rev-parse "$Remote/$Branch")
if (-not $parent) { Fail "no such branch: $Remote/$Branch" }

$stage = Join-Path ([IO.Path]::GetTempPath()) "halo-mirror-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$tar = "$stage.tar"
$index = "$stage.index"
New-Item -ItemType Directory -Path $stage -Force | Out-Null

try {
    # ── 1. master's contribution ──────────────────────────────────────────────────────────────────────
    Write-Host 'exporting master' -ForegroundColor Cyan
    Invoke-Git -C $repo archive --format=tar "--output=$tar" HEAD -- @FromMaster | Out-Null
    & tar -xf $tar -C $stage
    if ($LASTEXITCODE -ne 0) { Fail 'tar extraction failed' }

    # ── 2. the public-only surface ────────────────────────────────────────────────────────────────────
    $overlay = Join-Path $repo 'mirror'
    if (-not (Test-Path -LiteralPath $overlay)) { Fail "missing overlay directory: $overlay" }
    foreach ($required in $RequiredOverlay) {
        if (-not (Test-Path -LiteralPath (Join-Path $overlay $required))) {
            Fail "mirror/$required is missing -- publishing without it would drop it from the public repo"
        }
    }
    Copy-Item -Path (Join-Path $overlay '*') -Destination $stage -Recurse -Force
    Write-Host "overlaid mirror/ ($((Get-ChildItem $overlay -Recurse -File).Count) files)" -ForegroundColor Cyan

    # ── 3. strip ──────────────────────────────────────────────────────────────────────────────────────
    # src only. Tests are not shipped, and their comments state what a test is pinning down, which is the
    # one thing an outside contributor most needs; docs/decisions.md bans comments in shipped source, not
    # in the suite. Stripping them once cost us three defects in an outside patch.
    Write-Host 'stripping comments from src' -ForegroundColor Cyan
    $stripProject = Join-Path $repo 'tools\strip\strip.csproj'
    $stripOut = Join-Path ([IO.Path]::GetTempPath()) 'halo-strip-bin'
    & dotnet build $stripProject -c Release -o $stripOut --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Fail 'building tools/strip failed' }
    & (Join-Path $stripOut 'strip.exe') (Join-Path $stage 'src')
    if ($LASTEXITCODE -ne 0) { Fail 'the strip tool failed' }

    # ── 4. the public repo's own policy gate, run before the push instead of after ─────────────────────
    Write-Host 'checking public source policy' -ForegroundColor Cyan
    & pwsh -NoProfile -File (Join-Path $stage 'scripts\verify-public-source.ps1') -Root $stage -Filesystem
    if ($LASTEXITCODE -ne 0) { Fail 'the staged tree fails the public source policy' }

    # ── 5. does the stripped tree still build and pass? ───────────────────────────────────────────────
    # Stripping is a syntax-tree rewrite, so it is safe in principle; in practice this is the step that
    # proves the thing being published is the thing that was tested.
    if (-not $SkipBuild) {
        Write-Host 'building and testing the stripped tree' -ForegroundColor Cyan
        & dotnet build (Join-Path $stage 'Halo.sln') -c Release --nologo -warnaserror
        if ($LASTEXITCODE -ne 0) { Fail 'the stripped tree does not build at 0 warnings' }
        & dotnet test (Join-Path $stage 'tests\Halo.Tests\Halo.Tests.csproj') -c Release --no-build --nologo
        if ($LASTEXITCODE -ne 0) { Fail 'the stripped tree fails its tests' }
    }

    # ── 6. turn the staged directory into a commit, without touching this repo's index ─────────────────
    $env:GIT_INDEX_FILE = $index
    try {
        Invoke-Git --git-dir=$gitDir --work-tree=$stage add -A . | Out-Null
        $tree = (& git --git-dir=$gitDir --work-tree=$stage write-tree)
        if (-not $tree) { Fail 'write-tree produced nothing' }
    }
    finally { Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue }

    if ((& git -C $repo rev-parse "$parent^{tree}") -eq $tree) {
        Write-Host "$Remote/$Branch already matches this tree; nothing to publish." -ForegroundColor Yellow
        return
    }

    # ── 7. the guard: nothing may vanish from the public repo ──────────────────────────────────────────
    $changes = & git -C $repo diff --name-status $parent $tree
    $deleted = @($changes | Where-Object { $_ -match '^D\s' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    if ($deleted.Count -gt 0 -and -not $AllowDeletions) {
        Fail ("this publish would DELETE $($deleted.Count) file(s) from ${Remote}/${Branch}:`n - " +
              ($deleted -join "`n - ") +
              "`n`nthat is how an outside patch gets erased. Back-port them into master (or into mirror/)" +
              " and run again; pass -AllowDeletions only if the removal is intended.")
    }
    if ($deleted.Count -gt 0) {
        Write-Host "deleting $($deleted.Count) file(s) as instructed by -AllowDeletions" -ForegroundColor Yellow
    }

    $summary = & git -C $repo diff --stat $parent $tree
    Write-Host ($summary | Select-Object -Last 1) -ForegroundColor Green

    # ── 8. commit as phoseinq, no trailers ────────────────────────────────────────────────────────────
    # The mirror is published under one name and carries no Co-Authored-By, per this project's recipe.
    $env:GIT_AUTHOR_NAME = $Identity.Name
    $env:GIT_AUTHOR_EMAIL = $Identity.Email
    $env:GIT_COMMITTER_NAME = $Identity.Name
    $env:GIT_COMMITTER_EMAIL = $Identity.Email
    try {
        $commit = ($Message | & git -C $repo commit-tree $tree -p $parent)
    }
    finally {
        'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL' |
            ForEach-Object { Remove-Item "Env:\$_" -ErrorAction SilentlyContinue }
    }
    if (-not $commit) { Fail 'commit-tree produced nothing' }
    Write-Host "built mirror commit $commit" -ForegroundColor Green

    if ($Push) {
        Invoke-Git -C $repo push $Remote "${commit}:refs/heads/$Branch" | Out-Null
        Write-Host "pushed to $Remote/$Branch" -ForegroundColor Green
    }
    else {
        Write-Host "not pushed. to publish it:" -ForegroundColor Yellow
        Write-Host "    git push $Remote ${commit}:refs/heads/$Branch"
    }
}
finally {
    Remove-Item -LiteralPath $tar, $index -Force -ErrorAction SilentlyContinue
    if ($KeepStage) { Write-Host "stage kept at $stage" -ForegroundColor DarkGray }
    elseif (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}
