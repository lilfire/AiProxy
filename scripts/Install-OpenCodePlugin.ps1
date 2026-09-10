$source = Join-Path $PSScriptRoot "..\plugins\shell-ai-session.ts"
$opencodeDir = Join-Path $env:USERPROFILE ".config\opencode"
$destDir = Join-Path $opencodeDir "plugins"
$dest = Join-Path $destDir "shell-ai-session.ts"

if (!(Test-Path $source)) {
    Write-Error "Kildefil ikke funnet: $source"
    exit 1
}

$pluginDep = Join-Path $opencodeDir "node_modules\@opencode-ai\plugin"
if (!(Test-Path $pluginDep)) {
    Write-Host "Installerer npm-avhengigheter..."
    Push-Location $opencodeDir
    npm install
    Pop-Location
}

if (!(Test-Path $destDir)) {
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
}

Copy-Item -Path $source -Destination $dest -Force
Write-Host "Kopiert $source -> $dest"
