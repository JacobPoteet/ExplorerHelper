<#
.SYNOPSIS
    Cuts a release in two steps: a version-bump pull request, then the tag.
.DESCRIPTION
    The bump and the tag have to agree. release.yml refuses to build a tag whose version doesn't
    match Directory.Build.props, and tagging a commit that predates the bump is how v0.6.0 failed
    the first time (issue #46). main takes no direct commits, so the bump travels as a PR:

      ./release.ps1 -Version 0.7.0        bump on release/v0.7.0, push, open the PR
      (squash-merge the PR: /ship)
      ./release.ps1 -Version 0.7.0 -Tag   check main carries 0.7.0, then tag and push v0.7.0

    The tag step refuses unless main's Directory.Build.props already says the version, so the
    ordering mistake from #46 stays impossible. Every precondition runs before the first write, so a
    rejected step leaves the repo exactly as it was.
.EXAMPLE
    ./release.ps1 -Version 0.7.0 -WhatIf        # print the plan, touch nothing
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Second step: tag main once the bump PR has merged.
    [switch]$Tag,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$tagName = "v$Version"
$branch = "release/$tagName"

function Fail($message) {
    Write-Host "release: $message" -ForegroundColor Red
    exit 1
}

function Read-PropsVersion {
    if (-not (Test-Path $propsPath)) { Fail "cannot find $propsPath." }
    $props = Get-Content $propsPath -Raw
    if ($props -notmatch '<Version>(.*?)</Version>') { Fail 'no <Version> element in Directory.Build.props.' }
    return @{ Text = $props; Version = $Matches[1] }
}

# --- Preconditions shared by both steps -----------------------------------------------

$current = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
if ($current -ne 'main') {
    Fail "on '$current', expected 'main'. Switch to main first."
}

if ((git -C $root status --porcelain)) {
    Fail 'working tree has uncommitted changes. Commit or stash them first.'
}

git -C $root fetch origin --quiet --tags
$local = (git -C $root rev-parse HEAD).Trim()
$remote = (git -C $root rev-parse 'origin/main').Trim()
if ($local -ne $remote) {
    Fail 'main is out of sync with origin/main. Pull first.'
}

if (git -C $root tag --list $tagName) {
    Fail "$tagName already exists locally. Delete it first (git tag -d $tagName) if you mean to redo it."
}
if (git -C $root ls-remote --tags origin $tagName) {
    Fail "$tagName already exists on origin. Pick the next version, or delete the tag if it never shipped."
}

$props = Read-PropsVersion

# --- Step 2: tag ----------------------------------------------------------------------

if ($Tag) {
    if ($props.Version -ne $Version) {
        Fail "main's Directory.Build.props says $($props.Version), not $Version. Merge the bump PR first."
    }
    Write-Host "release: tagging $tagName on main ($($local.Substring(0, 7)))" -ForegroundColor Cyan
    if ($WhatIf) {
        Write-Host '  (WhatIf) would create and push the tag.' -ForegroundColor Yellow
        exit 0
    }
    git -C $root tag -a $tagName -m $tagName
    if ($LASTEXITCODE -ne 0) { Fail 'tagging failed.' }
    git -C $root push origin $tagName
    if ($LASTEXITCODE -ne 0) { Fail "tag push failed. Re-run 'git push origin $tagName' to start the release." }
    Write-Host ""
    Write-Host "Pushed $tagName. release.yml is building the zip and installer now:" -ForegroundColor Green
    Write-Host "  gh run watch (or) https://github.com/JacobPoteet/ExplorerHelper/actions"
    exit 0
}

# --- Step 1: bump PR ------------------------------------------------------------------

if ($props.Version -eq $Version) {
    Fail "Directory.Build.props already says $Version. Run with -Tag to tag it."
}
if (git -C $root branch --list $branch) {
    Fail "branch $branch already exists. Delete it or finish that release first."
}

Write-Host "release: $($props.Version) -> $Version on $branch, then a PR" -ForegroundColor Cyan
if ($WhatIf) {
    Write-Host '  (WhatIf) would branch, rewrite Directory.Build.props, commit, push, and open a PR.' -ForegroundColor Yellow
    exit 0
}

git -C $root switch -c $branch
if ($LASTEXITCODE -ne 0) { Fail "could not create $branch." }

($props.Text -replace '<Version>.*?</Version>', "<Version>$Version</Version>") |
    Set-Content -Path $propsPath -Encoding utf8 -NoNewline

git -C $root add Directory.Build.props
git -C $root commit -m "Release $Version"
if ($LASTEXITCODE -ne 0) { Fail 'commit failed.' }

git -C $root push -u origin $branch
if ($LASTEXITCODE -ne 0) { Fail "push failed. Re-run 'git push -u origin $branch' once resolved." }

$body = @"
## Summary

Bumps ``Directory.Build.props`` to $Version for $tagName.

## Verification

``release.ps1`` checked main was clean and in sync, and that $tagName does not exist.

## Also fixed

None.

## Wiki notes

The release pass updates the version in the index's identity table.
"@
$body | gh pr create --repo JacobPoteet/ExplorerHelper --base main --head $branch --title "Release $tagName" --label release --body-file -
if ($LASTEXITCODE -ne 0) { Fail "PR creation failed. Open it by hand for $branch." }

git -C $root switch main | Out-Null
Write-Host ""
Write-Host "Opened the bump PR. Squash-merge it once CI passes, pull main, then:" -ForegroundColor Green
Write-Host "  ./release.ps1 -Version $Version -Tag"
