param(
    [switch]$SelfTest,
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

function Get-PolicyViolations {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,
        [switch]$UseFilesystem
    )

    $violations = [System.Collections.Generic.List[string]]::new()

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
                $violations.Add("$relative`:$lineNumber tab indentation")
            }

            if ($line -match '^\s*(//|/\*|\*|\*/)') {
                $violations.Add("$relative`:$lineNumber shipped source comment")
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
                $violations.Add("$relative production package '$name' is not allowed")
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
        foreach ($expected in @('tab indentation', 'shipped source comment', 'production package')) {
            if (-not ($found -match [regex]::Escape($expected))) {
                throw "self-test did not detect: $expected"
            }
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

$policyViolations = Get-PolicyViolations -SourceRoot ([IO.Path]::GetFullPath($Root))
if ($policyViolations.Count -gt 0) {
    Write-Error ("Public source policy failed:`n - " + ($policyViolations -join "`n - "))
    exit 1
}

Write-Host 'Public source policy passed.'
