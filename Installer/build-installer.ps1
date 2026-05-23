$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$appProject = Join-Path $repoRoot "LoLChatTranslator\LoLChatTranslator.csproj"
$installerProject = Join-Path $PSScriptRoot "LoLChatTranslatorInstaller\LoLChatTranslatorInstaller.csproj"
$payloadDir = Join-Path $PSScriptRoot "payload"
$frameworkDependentDir = Join-Path $PSScriptRoot "framework-dependent"
$installerProjectDir = Join-Path $PSScriptRoot "LoLChatTranslatorInstaller"
$payloadZip = Join-Path $installerProjectDir "Payload.zip"
$outputDir = Join-Path $PSScriptRoot "dist"
$frameworkDependentZip = Join-Path $outputDir "LoLChatTranslator_FrameworkDependent_1.0.0_win-x64.zip"

if (Test-Path $payloadDir) {
    Remove-Item -LiteralPath $payloadDir -Recurse -Force
}

if (Test-Path $payloadZip) {
    Remove-Item -LiteralPath $payloadZip -Force
}

if (Test-Path $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

if (Test-Path $frameworkDependentDir) {
    Remove-Item -LiteralPath $frameworkDependentDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
New-Item -ItemType Directory -Force -Path $frameworkDependentDir | Out-Null
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $payloadDir

Get-ChildItem -Path $payloadDir -Recurse -Filter *.pdb | Remove-Item -Force
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZip -Force

dotnet publish $appProject `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $frameworkDependentDir

Get-ChildItem -Path $frameworkDependentDir -Recurse -Filter *.pdb | Remove-Item -Force
Compress-Archive -Path (Join-Path $frameworkDependentDir "*") -DestinationPath $frameworkDependentZip -Force

dotnet publish $installerProject `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $outputDir

Get-ChildItem -Path $outputDir -Recurse -Filter *.pdb | Remove-Item -Force
$sourceExe = Join-Path $outputDir "LoLChatTranslatorSetup.exe"
$finalExe = Join-Path $outputDir "LoLChatTranslator_Setup_1.0.0.exe"
if (Test-Path $sourceExe) {
    Move-Item -LiteralPath $sourceExe -Destination $finalExe -Force
}

Write-Host "Installer created: $finalExe"
Write-Host "Framework-dependent package created: $frameworkDependentZip"
