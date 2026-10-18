<#
Publishes the built mod DLL to the GitHub release for its version (v<MelonInfo version>).
Creates the release if it does not exist; otherwise replaces the DLL asset. The token comes from
Git Credential Manager (the account in -User), so nothing secret lives in the repo.

Run by the PublishGitHubRelease target in ModMenu.csproj after every Release build.
Skip it with: dotnet build -c Release -p:PublishRelease=false
#>
param(
    [Parameter(Mandatory)] [string] $DllPath,
    [Parameter(Mandatory)] [string] $SourceFile,
    [string] $LicenseFile,
    [string] $ExtraFile,
    [string] $Repo = "NotReallyMeButKinda/orange-bear-monkey-sospmenu",
    [string] $User = "NotReallyMeButKinda"
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$match = Select-String -Path $SourceFile -Pattern 'MelonInfo\(typeof\([^)]*\),\s*"[^"]*",\s*"([^"]+)"' | Select-Object -First 1
if (-not $match) { throw "No MelonInfo version found in $SourceFile" }
$version = $match.Matches[0].Groups[1].Value
$tag = "v$version"

# token for $User from Git Credential Manager (never printed)
$fill = "protocol=https`nhost=github.com`nusername=$User`n`n" | git credential fill 2>$null
$token = ($fill | Where-Object { $_ -like "password=*" } | Select-Object -First 1) -replace '^password=', ''
if (-not $token) { throw "No GitHub credential for $User; run a git push once to sign in." }

$headers = @{
    Authorization          = "Bearer $token"
    Accept                 = "application/vnd.github+json"
    "X-GitHub-Api-Version" = "2022-11-28"
    "User-Agent"           = "social-space-mod-menu-publisher"
}
$api = "https://api.github.com/repos/$Repo"

try {
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers
} catch {
    if ($_.Exception.Response.StatusCode.value__ -ne 404) { throw }
    # point the tag at HEAD only if HEAD is already on GitHub; otherwise at main
    $head = (git rev-parse HEAD).Trim()
    $onRemote = git branch -r --contains $head 2>$null | Where-Object { $_ -match 'origin/' }
    $target = if ($onRemote) { $head } else { "main" }
    $body = @{
        tag_name         = $tag
        target_commitish = $target
        name             = "Social Space Mod Menu $tag"
        body             = "Drop ``SocialSpaceModMenu.dll`` into the game's ``Mods`` folder (MelonLoader 0.7.3).`n`nIncludes code adapted from Another-Axiom/GorillaLocomotion (MIT); see the attached license."
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json
    $release = Invoke-RestMethod -Method Post -Uri "$api/releases" -Headers $headers -Body $body -ContentType "application/json"
    Write-Host "Created release $tag"
}

function Upload-Asset([string] $path, [string] $name, [bool] $replace) {
    $existing = $release.assets | Where-Object { $_.name -eq $name }
    if ($existing) {
        if (-not $replace) { return }
        Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($existing.id)" -Headers $headers | Out-Null
    }
    $uploadUrl = "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$([Uri]::EscapeDataString($name))"
    Invoke-RestMethod -Method Post -Uri $uploadUrl -Headers $headers -InFile $path -ContentType "application/octet-stream" | Out-Null
    Write-Host "Uploaded $name to $tag"
}

Upload-Asset $DllPath ([IO.Path]::GetFileName($DllPath)) $true
if ($ExtraFile -and (Test-Path $ExtraFile)) {
    Upload-Asset $ExtraFile ([IO.Path]::GetFileName($ExtraFile)) $true
}
if ($LicenseFile -and (Test-Path $LicenseFile)) {
    Upload-Asset $LicenseFile "LICENSE-GorillaLocomotion.txt" $false
}
Write-Host "Release: $($release.html_url)"
