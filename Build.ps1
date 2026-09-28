param(
    [Parameter(Mandatory=$true)][string]$Iscc,
    [string]$RuntimeVersion = '8.0.31'
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen: $LASTEXITCODE" } }
$app = Join-Path $PSScriptRoot 'dist\FanAtlas'
$plugin = Join-Path $PSScriptRoot 'streamdeck\de.crazybatto.fanatlas.sdPlugin'
dotnet publish src/FanAtlas/FanAtlas.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" --source https://api.nuget.org/v3/index.json -o $app
Check-Exit
dotnet publish src/FanAtlas.Deck/FanAtlas.Deck.csproj -c Release -r win-x64 --self-contained true "-p:RuntimeFrameworkVersion=$RuntimeVersion" --source https://api.nuget.org/v3/index.json -o (Join-Path $plugin 'bin')
Check-Exit
Copy-Item assets/fan.png (Join-Path $plugin 'bin/fan.png') -Force
npm.cmd install --prefix dist/tools @elgato/cli@1.10.1 --no-fund --no-audit --ignore-scripts
Check-Exit
& dist/tools/node_modules/.bin/streamdeck.cmd validate $plugin
Check-Exit
& dist/tools/node_modules/.bin/streamdeck.cmd pack $plugin --output dist
Check-Exit
New-Item -ItemType Directory -Force (Join-Path $app 'Extras') | Out-Null
Copy-Item dist/de.crazybatto.fanatlas.streamDeckPlugin (Join-Path $app 'Extras') -Force
Copy-Item ANLEITUNG.html $app -Force
& $Iscc "/DAppSource=$app" "/DOutputPath=$(Join-Path $PSScriptRoot 'dist')" installer/FanAtlas.iss
Check-Exit
Write-Host 'Fertig: Installer und Plugin liegen im Ordner dist.'

