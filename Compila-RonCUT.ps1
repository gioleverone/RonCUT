$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$target = Join-Path $PSScriptRoot 'RonCUT.exe'
$localSdk = Join-Path $env:LOCALAPPDATA 'RonCUT-Dev\dotnet\dotnet.exe'
if(Test-Path -LiteralPath $localSdk)
{
    $dotnetRoncut = $localSdk
    $env:DOTNET_ROOT = Split-Path -Parent $localSdk
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
}
else
{
    $dotnetRoncut = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
function Get-SourceFingerprint
{
    $fingerprints = foreach($folder in @('RonCUT','RonCUT.Tests'))
    {
        foreach($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $folder) -Recurse -Force -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object FullName)
        {
            $file.FullName + ':' + (Get-FileHash -LiteralPath $file.FullName).Hash
        }
    }
    return (@($fingerprints) + (Get-FileHash -LiteralPath $PSCommandPath).Hash) -join "`n"
}
$validatedSources = Get-SourceFingerprint
& $dotnetRoncut build RonCUT.Tests\RonCUT.Tests.csproj -c Release --nologo
if($LASTEXITCODE -ne 0)
{
    throw 'Compilazione non riuscita: RonCUT.exe non viene aggiornato.'
}
$testOutput = & $dotnetRoncut run --project RonCUT.Tests\RonCUT.Tests.csproj -c Release --no-build 2>&1
$testExit = $LASTEXITCODE
@('Verifiche software RonCUT. CNC e HTTP simulati: nessun comando alla macchina reale.','') + @($testOutput) | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'VERIFICHE.txt') -Encoding utf8
$testOutput | Select-Object -Last 8
if($testExit -ne 0)
{
    throw 'Verifiche non superate: RonCUT.exe non viene aggiornato.'
}
if((Get-SourceFingerprint) -cne $validatedSources)
{
    throw 'I sorgenti sono cambiati durante le verifiche: riesegui la compilazione prima di aggiornare RonCUT.exe.'
}
& $dotnetRoncut publish RonCUT\RonCUT.csproj -p:PublishProfile=Windows --nologo
if($LASTEXITCODE -ne 0)
{
    throw 'Pubblicazione non riuscita: RonCUT.exe non viene aggiornato.'
}
if((Get-SourceFingerprint) -cne $validatedSources)
{
    throw 'I sorgenti sono cambiati durante la pubblicazione: RonCUT.exe non viene aggiornato.'
}
$published = Join-Path $PSScriptRoot 'RonCUT\obj\publish\RonCUT.exe'
$files = @(Get-ChildItem -LiteralPath (Split-Path -Parent $published) -File)
if($files.Count -ne 1 -or $files[0].Name -ne 'RonCUT.exe')
{
    throw 'La pubblicazione non contiene un solo eseguibile: controllare il profilo Windows.'
}
if(Get-Process -Name RonCUT -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $target })
{
    throw 'Compilazione e verifiche completate. Chiudi RonCUT per sostituire RonCUT.exe.'
}
Copy-Item -LiteralPath $published -Destination $target -Force
Get-Item -LiteralPath $target | Select-Object FullName,@{Name='Versione';Expression={$_.VersionInfo.FileVersion}},@{Name='MB';Expression={[math]::Round($_.Length / 1MB,1)}}
