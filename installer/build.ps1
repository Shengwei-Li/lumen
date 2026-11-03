# Builds artifacts\Lumen-<version>-Setup.exe and the portable zip.
# Requires the .NET 8 SDK and Inno Setup 6.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Lumen.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$artifacts = Join-Path $root 'artifacts'
$app = Join-Path $artifacts 'app'

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$root\Lumen.csproj" -c Release -o $app --self-contained true --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }
Get-ChildItem $app -Recurse -Include *.pdb, *.xml | Remove-Item
Copy-Item "$root\LICENSE" $app

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" "$PSScriptRoot\Lumen.iss"
if ($LASTEXITCODE) { throw 'installer build failed' }

$portable = Join-Path $artifacts 'Lumen'
Rename-Item $app $portable
Compress-Archive $portable (Join-Path $artifacts "Lumen-$version-win-x64-portable.zip")
Remove-Item $portable -Recurse

Get-ChildItem $artifacts -File | ForEach-Object { '{0}  {1:N1} MB' -f $_.Name, ($_.Length / 1MB) }
