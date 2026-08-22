<#
.SYNOPSIS
    Cuts a release: bumps the version, commits, tags, and pushes both in one step.
.DESCRIPTION
    The bump and the tag have to travel together — release.yml refuses to build a tag whose
    version doesn't match Directory.Build.props, and tagging a commit that predates the bump is
    how v0.6.0 failed the first time (issue #46). Doing both here makes that ordering mistake
    impossible.

    Everything is checked before anything is written, so a failed precondition leaves the repo
    exactly as it was.
.EXAMPLE
    ./release.ps1 -Version 0.7.0
.EXAMPLE
    ./release.ps1 -Version 0.7.0 -WhatIf   # print the plan, touch nothing
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Cut from somewhere other than main (a hotfix branch, say).
    [string]$Branch = 'main',

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$tag = "v$Version"

function Fail($message) {
    Write-Host "release: $message" -ForegroundColor Red
    exit 1
}

# --- Preconditions ------------------------------------------------------------------
# All of them run before the first write, so a rejected release leaves nothing half-done.

$current = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($current -ne $Branch) {
    Fail "on '$current', expected '$Branch'. Switch branches or pass -Branch $current."
}

if ((git -C $root status --porcelain)) {
    Fail 'working tree has uncommitted changes. Commit or stash them first.'
}

git -C $root fetch origin --quiet --tags
$local = (git -C $root rev-parse HEAD).Trim()
$remote = (git -C $root rev-parse "origin/$Branch").Trim()
if ($local -ne $remote) {
    Fail "$Branch is out of sync with origin/$Branch. Pull (or push) first."
}

$existing = git -C $root tag --list $tag
if ($existing) {
    Fail "$tag already exists locally. Delete it first (git tag -d $tag) if you mean to redo it."
}
if (git -C $root ls-remote --tags origin $tag) {
    Fail "$tag already exists on origin. Pick the next version, or delete the tag if it never shipped."
}

if (-not (Test-Path $propsPath)) { Fail "cannot find $propsPath." }
$props = Get-Content $propsPath -Raw
if ($props -notmatch '<Version>(.*?)</Version>') { Fail 'no <Version> element in Directory.Build.props.' }
$fromVersion = $Matches[1]
if ($fromVersion -eq $Version) {
    Fail "Directory.Build.props already says $Version. Nothing to bump."
}

Write-Host "release: $fromVersion -> $Version on $Branch, tagging $tag" -ForegroundColor Cyan
if ($WhatIf) {
    Write-Host '  (WhatIf) would rewrite Directory.Build.props, commit, tag, and push.' -ForegroundColor Yellow
    exit 0
}

# --- Bump, commit, tag, push --------------------------------------------------------

($props -replace '<Version>.*?</Version>', "<Version>$Version</Version>") |
    Set-Content -Path $propsPath -Encoding utf8 -NoNewline

git -C $root add Directory.Build.props
git -C $root commit -m "Release $Version"
if ($LASTEXITCODE -ne 0) { Fail 'commit failed.' }

git -C $root tag $tag
if ($LASTEXITCODE -ne 0) { Fail "tagging failed. The bump is committed but unpushed - git reset --hard HEAD~1 to undo." }

# Commit first: a tag arriving at origin before the commit it points at would start a release
# build against a ref the runner can't check out.
git -C $root push origin $Branch
if ($LASTEXITCODE -ne 0) { Fail "push failed. Local commit and tag stand; re-run 'git push origin $Branch' once resolved." }

git -C $root push origin $tag
if ($LASTEXITCODE -ne 0) { Fail "tag push failed. Re-run 'git push origin $tag' to start the release." }

Write-Host ""
Write-Host "Pushed $tag. release.yml is building the zip and installer now:" -ForegroundColor Green
Write-Host "  gh run watch (or) https://github.com/JacobPoteet/ExplorerHelper/actions"
