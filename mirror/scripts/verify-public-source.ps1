param(
    [switch]$SelfTest,
    [switch]$CommentsAdvisory,
    # walk the filesystem instead of `git ls-files` -- the release script checks a staged copy of the tree
    # that is not a git repository yet, so the gate can run before the push rather than after it
    [switch]$Filesystem,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function New-Violation {
    param([string]$Rule, [string]$File, [int]$Line, [string]$Message)
    [pscustomobject]@{ Rule = $Rule; File = $File; Line = $Line; Message = $Message }
}

function Get-PolicyViolations {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [switch]$UseFilesystem
    )

    $violations = [System.Collections.Generic.List[object]]::new()

    if ($UseFilesystem) {
        $sourceFiles = Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src') -Recurse -Filter '*.cs' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName }
    }
    else {
        $sourceFiles = git -C $SourceRoot ls-files -- 'src/*.cs' 'src/**/*.cs' |
            ForEach-Object { Join-Path $SourceRoot $_ }
    }

    foreach ($file in $sourceFiles) {
        $relative = [IO.Path]::GetRelativePath($SourceRoot, $file)
        $lineNumber = 0

        foreach ($line in [IO.File]::ReadLines($file)) {
            $lineNumber++

            if ($line.Contains("`t")) {
                $violations.Add((New-Violation 'tab' $relative $lineNumber 'tab indentation'))
            }

            if ($line -match '^\s*(//|/\*|\*|\*/)') {
                $violations.Add((New-Violation 'comment' $relative $lineNumber 'shipped source comment'))
            }
        }
    }

    $projectFiles = if ($UseFilesystem) {
        Get-ChildItem -LiteralPath (Join-Path $SourceRoot 'src') -Recurse -Filter '*.csproj' |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { $_.FullName }
    }
    else {
        git -C $SourceRoot ls-files -- 'src/*.csproj' 'src/**/*.csproj' |
            ForEach-Object { Join-Path $SourceRoot $_ }
    }

    foreach ($projectFile in $projectFiles) {
        [xml]$project = Get-Content -LiteralPath $projectFile -Raw
        $relative = [IO.Path]::GetRelativePath($SourceRoot, $projectFile)

        foreach ($reference in $project.Project.ItemGroup.PackageReference) {
            $name = [string]$reference.Include
            if ($name -and $name -ne 'System.Drawing.Common') {
                $violations.Add((New-Violation 'package' $relative 0 "production package '$name' is not allowed"))
            }
        }
    }

    return $violations
}

if ($SelfTest) {
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) "halo-policy-$([guid]::NewGuid().ToString('N'))"

    try {
        $sourceDir = Join-Path $tempRoot 'src/Test'
        [IO.Directory]::CreateDirectory($sourceDir) | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $sourceDir 'Bad.cs'),
            "`tclass Bad { }`r`n// shipped comment`r`n"
        )
        [IO.File]::WriteAllText(
            (Join-Path $sourceDir 'Test.csproj'),
            '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Unexpected.Package" Version="1.0.0" /></ItemGroup></Project>'
        )

        $found = Get-PolicyViolations -SourceRoot $tempRoot -UseFilesystem
        foreach ($expected in @('tab', 'comment', 'package')) {
            if (-not ($found.Rule -contains $expected)) {
                throw "self-test did not detect rule: $expected"
            }
        }

        $hard = @($found | Where-Object { $_.Rule -ne 'comment' })
        if ($hard.Count -lt 2) {
            throw 'self-test expected the tab and package rules to survive the advisory split'
        }

        Write-Host 'Policy self-test passed.'
    }
    finally {
        if ($tempRoot.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Directory]::Exists($tempRoot)) {
            [IO.Directory]::Delete($tempRoot, $true)
        }
    }

    exit 0
}

$policyViolations = Get-PolicyViolations -SourceRoot ([IO.Path]::GetFullPath($Root)) -UseFilesystem:$Filesystem

# On a pull request the comment rule is reported, not enforced. This repository is a mechanically stripped
# mirror of a comment-bearing tree, so shipped source carries no comments by construction -- but that is
# our publishing mechanism, not something an outside patch should be failed for. Rejecting a change
# because it explains itself teaches the wrong habit, and three of the six defects in the last outside
# patch here came from load-bearing comments having been stripped away before the contributor saw them.
# Tabs and the package allowlist stay hard everywhere: those are real policy, not mirroring artefacts.
$advisory = @()
$fatal = @($policyViolations)

if ($CommentsAdvisory) {
    $advisory = @($policyViolations | Where-Object { $_.Rule -eq 'comment' })
    $fatal = @($policyViolations | Where-Object { $_.Rule -ne 'comment' })
}

foreach ($item in $advisory) {
    Write-Host "::warning file=$($item.File),line=$($item.Line)::$($item.Message)"
}

if ($fatal.Count -gt 0) {
    $lines = $fatal | ForEach-Object {
        if ($_.Line -gt 0) { "$($_.File):$($_.Line) $($_.Message)" } else { "$($_.File) $($_.Message)" }
    }
    Write-Error ("Public source policy failed:`n - " + ($lines -join "`n - "))
    exit 1
}

if ($advisory.Count -gt 0) {
    Write-Host "Public source policy passed with $($advisory.Count) advisory comment finding(s)."
}
else {
    Write-Host 'Public source policy passed.'
}
