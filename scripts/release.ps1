<#
.SYNOPSIS
    Builds Symlinker, zips it and publishes a release to GitHub and Gitee.

.DESCRIPTION
    Steps:
      1. Checks that git is clean and the tag does not exist yet.
      2. Sets <Version> in Symlinker.csproj.
      3. dotnet publish (win-x64) and zips the result with a SHA-256 file.
      4. Commits the version change, creates the tag, pushes to both remotes.
      5. Creates the GitHub release (GitHub CLI) and the Gitee release (Gitee API) and uploads the zip.

    Nothing is changed in git or on the websites until you confirm (use -Yes to skip the question).
    Use -DryRun to only build and zip.

.EXAMPLE
    .\scripts\release.ps1 -Version 0.2.0 -DryRun

.EXAMPLE
    $env:GITEE_TOKEN = "<your Gitee private token>"
    .\scripts\release.ps1 -Version 0.2.0 -NotesFile .\notes.md

.NOTES
    Requirements: .NET 10 SDK, git, and for GitHub the GitHub CLI ("gh auth login" once).
    For Gitee create a private token at https://gitee.com/profile/personal_access_tokens
    (permission "projects") and put it in the GITEE_TOKEN environment variable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    # Release notes. If both -Notes and -NotesFile are missing, the commit subjects since the last tag are used.
    [string]$Notes,
    [string]$NotesFile,

    # Names of the git remotes for the two sites.
    [string]$GitHubRemote = 'origin',
    [string]$GiteeRemote  = 'gitee',

    # Owners and repository name (must match AppInfo.cs, because the update check reads these).
    [string]$GitHubOwner = 'DwgSearch',
    [string]$GiteeOwner  = 'h1985',
    [string]$RepoName    = 'Symlinker',

    [switch]$SkipGitHub,
    [switch]$SkipGitee,

    # Bundle the .NET runtime (about 70 MB larger, but runs without installing .NET).
    [switch]$SelfContained,

    [switch]$DryRun,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Native {
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE."
    }
}

function Get-NativeOutput {
    param([string]$File, [string[]]$Arguments)
    # Windows PowerShell 5.1 turns native stderr output into errors when the preference is Stop.
    $ErrorActionPreference = 'Continue'
    $output = & $File @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return ($output -join "`n").Trim()
}

# ---------- locate things ----------
$repoRoot = Get-NativeOutput 'git' @('rev-parse', '--show-toplevel')
if (-not $repoRoot) { throw 'Run this script inside the git repository.' }
Set-Location $repoRoot

$csproj = Get-ChildItem -Path $repoRoot -Recurse -Filter 'Symlinker.csproj' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Select-Object -First 1
if (-not $csproj) { throw 'Symlinker.csproj was not found.' }

$tag       = "v$Version"
$flavor    = if ($SelfContained) { 'win-x64-selfcontained' } else { 'win-x64' }
$distDir   = Join-Path $repoRoot 'dist'
$publishDir = Join-Path $distDir "publish-$flavor"
$zipPath   = Join-Path $distDir "Symlinker-$tag-$flavor.zip"
$shaPath   = "$zipPath.sha256"

# ---------- pre-flight checks ----------
$hasGitHub = -not $SkipGitHub -and (Get-NativeOutput 'git' @('remote', 'get-url', $GitHubRemote))
$hasGitee  = -not $SkipGitee  -and (Get-NativeOutput 'git' @('remote', 'get-url', $GiteeRemote))
if (-not $SkipGitHub -and -not $hasGitHub) { Write-Warning "Git remote '$GitHubRemote' not found: GitHub is skipped. Add it with: git remote add $GitHubRemote https://github.com/$GitHubOwner/$RepoName.git" }
if (-not $SkipGitee  -and -not $hasGitee)  { Write-Warning "Git remote '$GiteeRemote' not found: Gitee is skipped. Add it with: git remote add $GiteeRemote https://gitee.com/$GiteeOwner/$RepoName.git" }

if (-not $DryRun) {
    if (-not $hasGitHub -and -not $hasGitee) { throw 'No remote to publish to.' }
    if (Get-NativeOutput 'git' @('status', '--porcelain')) { throw 'The git working tree is not clean. Commit or stash your changes first.' }
    if (Get-NativeOutput 'git' @('tag', '--list', $tag)) { throw "The tag $tag already exists." }
    if ($hasGitHub -and -not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) was not found. Install it from https://cli.github.com and run "gh auth login", or use -SkipGitHub.' }
    if ($hasGitee -and -not $env:GITEE_TOKEN) { throw 'Set the GITEE_TOKEN environment variable, or use -SkipGitee.' }
}

$branch = Get-NativeOutput 'git' @('rev-parse', '--abbrev-ref', 'HEAD')

# ---------- release notes ----------
if ($NotesFile) {
    $Notes = Get-Content -Raw -Encoding UTF8 -Path $NotesFile
} elseif (-not $Notes) {
    $lastTag = Get-NativeOutput 'git' @('describe', '--tags', '--abbrev=0')
    $range   = if ($lastTag) { "$lastTag..HEAD" } else { 'HEAD' }
    $log     = Get-NativeOutput 'git' @('log', $range, '--no-merges', '--pretty=format:- %s')
    $Notes   = if ($log) { "Changes since $(if ($lastTag) { $lastTag } else { 'the beginning' }):`n`n$log" } else { "Symlinker $tag" }
}

# ---------- summary + confirmation ----------
Write-Host ''
Write-Host "Release $tag" -ForegroundColor Cyan
Write-Host "  project : $($csproj.FullName)"
Write-Host "  build   : $flavor"
Write-Host "  branch  : $branch"
Write-Host "  GitHub  : $(if ($hasGitHub) { "$GitHubOwner/$RepoName (remote $GitHubRemote)" } else { 'skipped' })"
Write-Host "  Gitee   : $(if ($hasGitee) { "$GiteeOwner/$RepoName (remote $GiteeRemote)" } else { 'skipped' })"
Write-Host '  notes   :'
$Notes -split "`n" | ForEach-Object { Write-Host "    $_" }
Write-Host ''
if ($DryRun) {
    Write-Host 'Dry run: only the build and the zip file are made.' -ForegroundColor Yellow
} elseif (-not $Yes) {
    $answer = Read-Host 'Commit, tag, push and publish? (y/N)'
    if ($answer -notmatch '^(y|yes)$') { Write-Host 'Cancelled.'; return }
}

# ---------- 1. set the version ----------
$csprojText = Get-Content -Raw -Encoding UTF8 -Path $csproj.FullName
if ($csprojText -notmatch '<Version>[^<]*</Version>') { throw 'No <Version> element in the project file.' }
$newText = [regex]::Replace($csprojText, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($csproj.FullName, $newText, (New-Object System.Text.UTF8Encoding($true)))

try {
    # ---------- 2. build and zip ----------
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null

    $publishArgs = @('publish', $csproj.FullName, '-c', 'Release', '-r', 'win-x64',
        '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
        '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $publishDir)
    Invoke-Native 'dotnet' $publishArgs

    foreach ($doc in 'README.md', 'README.zh-CN.md', 'LICENSE') {
        $file = Join-Path $repoRoot $doc
        if (Test-Path $file) { Copy-Item $file $publishDir }
    }

    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
    $hash = (Get-FileHash -Algorithm SHA256 -Path $zipPath).Hash.ToLowerInvariant()
    Set-Content -Encoding ASCII -Path $shaPath -Value "$hash  $(Split-Path -Leaf $zipPath)"
    Write-Host "Built $zipPath" -ForegroundColor Green
    Write-Host "SHA-256 $hash"
}
catch {
    # Put the project file back so a failed build leaves nothing behind.
    Invoke-Native 'git' @('checkout', '--', $csproj.FullName)
    throw
}

if ($DryRun) {
    Invoke-Native 'git' @('checkout', '--', $csproj.FullName)
    Write-Host 'Dry run finished. The project file was restored.' -ForegroundColor Yellow
    return
}

# ---------- 3. commit, tag, push ----------
Invoke-Native 'git' @('add', $csproj.FullName)
Invoke-Native 'git' @('commit', '-m', "Release $tag")
Invoke-Native 'git' @('tag', '-a', $tag, '-m', "Symlinker $tag")

$remotes = @()
if ($hasGitHub) { $remotes += $GitHubRemote }
if ($hasGitee)  { $remotes += $GiteeRemote }
foreach ($remote in $remotes) {
    Write-Host "Pushing to $remote ..."
    Invoke-Native 'git' @('push', $remote, $branch)
    Invoke-Native 'git' @('push', $remote, $tag)
}

# ---------- 4. GitHub release ----------
if ($hasGitHub) {
    Write-Host 'Creating the GitHub release ...'
    $notesTemp = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText($notesTemp.FullName, $Notes, (New-Object System.Text.UTF8Encoding($false)))
        Invoke-Native 'gh' @('release', 'create', $tag, $zipPath, $shaPath,
            '--repo', "$GitHubOwner/$RepoName", '--title', "Symlinker $tag", '--notes-file', $notesTemp.FullName)
    }
    finally { Remove-Item -Force $notesTemp -ErrorAction SilentlyContinue }
    Write-Host "GitHub: https://github.com/$GitHubOwner/$RepoName/releases/tag/$tag" -ForegroundColor Green
}

# ---------- 5. Gitee release ----------
if ($hasGitee) {
    Write-Host 'Creating the Gitee release ...'
    $base = "https://gitee.com/api/v5/repos/$GiteeOwner/$RepoName/releases"
    $body = @{
        access_token     = $env:GITEE_TOKEN
        tag_name         = $tag
        name             = "Symlinker $tag"
        body             = $Notes
        target_commitish = $branch
        prerelease       = 'false'
    }
    try {
        $release = Invoke-RestMethod -Method Post -Uri $base -Body $body
        Write-Host "Gitee: https://gitee.com/$GiteeOwner/$RepoName/releases/tag/$tag" -ForegroundColor Green
        try {
            # Uploading with curl.exe (included in Windows 10/11) keeps this working in Windows PowerShell 5.1 too.
            foreach ($asset in $zipPath, $shaPath) {
                Invoke-Native 'curl.exe' @('-sS', '--fail', '-o', 'NUL',
                    '-F', "access_token=$($env:GITEE_TOKEN)", '-F', "file=@$asset",
                    "$base/$($release.id)/attach_files")
            }
        }
        catch {
            Write-Warning "The Gitee release was created, but uploading the files failed: $($_.Exception.Message)"
            Write-Warning "Upload $zipPath and $shaPath by hand on the release page."
        }
    }
    catch {
        Write-Warning "Creating the Gitee release failed: $($_.Exception.Message)"
        Write-Warning "The tag was pushed. Create the release by hand at https://gitee.com/$GiteeOwner/$RepoName/releases/new and attach $zipPath."
    }
}

Write-Host ''
Write-Host "Done: $tag" -ForegroundColor Cyan
