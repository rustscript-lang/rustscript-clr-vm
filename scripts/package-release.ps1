param(
    [Parameter(Mandatory = $true)]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory = $true)]
    [string] $RustTarget
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/release'))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "pd-vm-clr-$RuntimeIdentifier"))
$requiredPrefix = $releaseRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $packageRoot.StartsWith($requiredPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Invalid release output path: $packageRoot"
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

$manifest = Join-Path $repoRoot 'native/pd-vm-compiler/Cargo.toml'
cargo build --locked --release --target $RustTarget --manifest-path $manifest
if ($LASTEXITCODE -ne 0) {
    throw 'Native pd-vm compiler build failed'
}

dotnet publish (Join-Path $repoRoot 'PdVm.Runner/PdVm.Runner.csproj') `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained false `
    -p:DebugSymbols=true `
    -p:DebugType=portable `
    --output $packageRoot
if ($LASTEXITCODE -ne 0) {
    throw 'PdVm.Runner publish failed'
}

$nativeName = if ($RuntimeIdentifier -eq 'win-x64') {
    'Pdvm.Compiler.Native.dll'
} elseif ($RuntimeIdentifier -eq 'osx-arm64') {
    'libPdvm.Compiler.Native.dylib'
} else {
    'libPdvm.Compiler.Native.so'
}
$cargoNativeName = if ($RuntimeIdentifier -eq 'win-x64') {
    'pd_vm_compiler.dll'
} elseif ($RuntimeIdentifier -eq 'osx-arm64') {
    'libpd_vm_compiler.dylib'
} else {
    'libpd_vm_compiler.so'
}
$nativeRoot = Join-Path $repoRoot "native/pd-vm-compiler/target/$RustTarget/release"
Copy-Item -LiteralPath (Join-Path $nativeRoot $cargoNativeName) -Destination (Join-Path $packageRoot $nativeName)
Get-ChildItem -LiteralPath $nativeRoot -Filter '*.pdb' -File | Copy-Item -Destination $packageRoot

$requiredFiles = @(
    'PdVm.Runner.dll',
    'PdVm.Runner.pdb',
    'PdVm.Compiler.dll',
    'PdVm.Compiler.pdb',
    'PdVm.Runtime.dll',
    'PdVm.Runtime.pdb',
    $nativeName
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $file))) {
        throw "Release package is missing $file"
    }
}

$archiveExtension = if ($RuntimeIdentifier -eq 'win-x64') { 'zip' } else { 'tar.gz' }
$archive = Join-Path $releaseRoot "pd-vm-clr-$RuntimeIdentifier.$archiveExtension"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
if ($RuntimeIdentifier -eq 'win-x64') {
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $archive -CompressionLevel Optimal
} else {
    tar -czf $archive -C $packageRoot .
    if ($LASTEXITCODE -ne 0) {
        throw 'Release archive creation failed'
    }
}
Write-Output $archive
