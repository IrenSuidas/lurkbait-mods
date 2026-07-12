<#
Builds the LurkBait BepInEx plugins and packages release artifacts under dist/:
  LurkBait-Mods-v<ver>.zip     BepInEx + all plugins (extract into the game folder)
  LurkBait-Plugins-v<ver>.zip  plugin DLLs only (for existing BepInEx installs)
  SHA256SUMS.txt               SHA256 of the DLLs and zips, for tamper checks
The game's assemblies are referenced in place (never committed); BepInEx is
downloaded from its official GitHub release and cached in dist/.
#>
[CmdletBinding()]
param(
    [string]$GameManagedDir,
    [string]$Version = "1.0.0",
    [string]$BepInExVersion = "5.4.23.2"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"

# Locate the game's ...\Managed folder across every Steam library (no hardcoded paths).
function Find-GameManaged {
    $game = "LurkBait Twitch Fishing"
    $rel  = "steamapps\common\$game\${game}_Data\Managed"
    $roots = @(
        Get-ItemPropertyValue 'HKCU:\Software\Valve\Steam' SteamPath -ErrorAction SilentlyContinue
        Get-ItemPropertyValue 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' InstallPath -ErrorAction SilentlyContinue
    ) | Where-Object { $_ }
    $libs = foreach ($r in $roots) {
        $vdf = Join-Path $r "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') |
                ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
        }
    }
    foreach ($base in (@($roots) + @($libs) | Select-Object -Unique)) {
        $managed = Join-Path $base $rel
        if (Test-Path (Join-Path $managed "Assembly-CSharp.dll")) { return $managed }
    }
}

if (-not $GameManagedDir) { $GameManagedDir = Find-GameManaged }
if (-not $GameManagedDir -or -not (Test-Path (Join-Path $GameManagedDir "Assembly-CSharp.dll"))) {
    throw "Game not found via Steam. Pass -GameManagedDir '<...\LurkBait Twitch Fishing_Data\Managed>'."
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# 1) Build all plugins.
dotnet build (Join-Path $repo "LurkBaitMods.slnx") -c Release -nologo -p:GameManagedDir="$GameManagedDir"
if ($LASTEXITCODE) { throw "Plugin build failed." }

$names = "NoChatbotOutage", "StableUserIds", "RemoteControl"
$dlls = foreach ($n in $names) {
    $dll = Join-Path $repo "src\$n\bin\Release\LurkBait.$n.dll"
    if (-not (Test-Path $dll)) { throw "Missing build output: $dll" }
    $dll
}

function Reset-Dir($path) {
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}
function Write-ZipFrom($source, $zip) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $source "*") -DestinationPath $zip
}

# 2) Plugins-only zip.
$pluginsStaging = Join-Path $dist "staging-plugins"
Reset-Dir $pluginsStaging
$dlls | ForEach-Object { Copy-Item $_ $pluginsStaging }
Set-Content -Path (Join-Path $pluginsStaging "README.txt") -Encoding UTF8 -Value @"
LurkBait plugins (for existing BepInEx installs)

Drop these DLLs into:  <game>\BepInEx\plugins\
  LurkBait.NoChatbotOutage.dll  - hides the stale "Temporary Chatbot Outage" popup
  LurkBait.StableUserIds.dll    - keeps gold/points when a viewer changes their Twitch name
  LurkBait.RemoteControl.dll    - localhost HTTP endpoint to adjust gold from external tools
Delete any file to disable that mod. Requires BepInEx 5 (x64) for a Unity Mono game.
"@
$pluginsZip = Join-Path $dist "LurkBait-Plugins-v$Version.zip"
Write-ZipFrom $pluginsStaging $pluginsZip

# 3) Fetch BepInEx (cached). If it can't be downloaded, skip the bundle but still
#    produce the plugins zip and checksums.
$bundleZip = $null
$bepInExZip = Join-Path $dist "BepInEx_win_x64_$BepInExVersion.zip"
if (-not (Test-Path $bepInExZip)) {
    $url = "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_x64_$BepInExVersion.zip"
    Write-Host "Downloading BepInEx $BepInExVersion ..."
    try {
        Invoke-WebRequest -Uri $url -OutFile $bepInExZip -UseBasicParsing
    } catch {
        Write-Warning "Could not download BepInEx ($($_.Exception.Message)); skipping the bundle."
        $bepInExZip = $null
    }
}

# 4) Full bundle: BepInEx + plugins.
if ($bepInExZip) {
    $bundleStaging = Join-Path $dist "staging-bundle"
    Reset-Dir $bundleStaging
    Expand-Archive -Path $bepInExZip -DestinationPath $bundleStaging -Force
    $pluginsDir = Join-Path $bundleStaging "BepInEx\plugins"
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    $dlls | ForEach-Object { Copy-Item $_ $pluginsDir }
    Set-Content -Path (Join-Path $bundleStaging "INSTALL.txt") -Encoding UTF8 -Value @"
LurkBait Twitch Fishing - Mods (BepInEx bundle)
Version: $Version   (bundles BepInEx $BepInExVersion, x64)

WHAT'S INCLUDED
  BepInEx\plugins\LurkBait.NoChatbotOutage.dll
      Hides the stale "Temporary Chatbot Outage" popup that shows on every launch.
  BepInEx\plugins\LurkBait.StableUserIds.dll
      Keeps a viewer's gold, points and casts when they change their Twitch username
      (tracks their stable numeric Twitch id). Shows an in-game message when it merges
      a rename. Config is generated on first run at
      BepInEx\config\dev.irensuidas.lurkbait.stableuserids.cfg
  BepInEx\plugins\LurkBait.RemoteControl.dll
      Local HTTP endpoint (127.0.0.1 only) so tools like Streamer.bot or SAMMI can adjust
      a player's gold and read the result back. Runs automatically and shows an in-game
      message when it starts. Port and toasts live in
      BepInEx\config\dev.irensuidas.lurkbait.remotecontrol.cfg
  Don't want one of them? Just delete its .dll from BepInEx\plugins\.

INSTALL (one extract)
  1. Fully close the game.
  2. Extract the CONTENTS of this zip into the game folder, the one containing
     "LurkBait Twitch Fishing.exe". In Steam you can open it with right-click the game,
     Manage, Browse local files. You should end up with winhttp.dll and a BepInEx\ folder
     next to the .exe.
  3. Launch the game once, then quit (this lets BepInEx finish setting up).
  4. Launch again - the fixes are active.

UNINSTALL
  Delete winhttp.dll, doorstop_config.ini, .doorstop_version, the BepInEx\ folder,
  and changelog.txt from the game folder. No game files are modified.

NOTES
  * Verify your download: run  Get-FileHash -Algorithm SHA256 <file>  and compare it to
    the matching line in SHA256SUMS.txt on the release page.
  * Your antivirus may flag winhttp.dll (BepInEx's loader). It's a false positive.
  * Steam "Verify integrity of game files" leaves these extra files alone.
"@
    $bundleZip = Join-Path $dist "LurkBait-Mods-v$Version.zip"
    Write-ZipFrom $bundleStaging $bundleZip
}

# 5) SHA256 checksums for the plugin DLLs and zips (sha256sum -c compatible).
$artifacts = @($dlls) + @($pluginsZip)
if ($bundleZip) { $artifacts += $bundleZip }
$sums = foreach ($f in $artifacts) {
    "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $f).Hash.ToLower(), [IO.Path]::GetFileName($f)
}
$sumPath = Join-Path $dist "SHA256SUMS.txt"
Set-Content -Path $sumPath -Value $sums -Encoding ascii

Write-Host "`nArtifacts ready:" -ForegroundColor Green
if ($bundleZip) { Write-Host "  $bundleZip   (BepInEx + all plugins)" }
Write-Host "  $pluginsZip  (plugins only)"
Write-Host "  $sumPath  (SHA256 checksums)"
