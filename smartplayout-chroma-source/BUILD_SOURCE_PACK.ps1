param([string]$Version = '0.6.8.50.57')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$safeVersion = $Version.Replace('.','_')
$dist = Join-Path $root 'DIST'
$stage = Join-Path ([IO.Path]::GetTempPath()) ("SMART_PLAYOUT_SOURCE_" + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stage ("SMART_PLAYOUT_v${safeVersion}_SOURCE_PACK")
$zip = Join-Path $dist ("SMART_PLAYOUT_v${safeVersion}_SOURCE_PACK.zip")
$excluded = @('PLAYOUT','DIST','_publish','.git','.vs','bin','obj')

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    Get-ChildItem -LiteralPath $root -Force | Where-Object { $excluded -notcontains $_.Name } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $packageRoot -Recurse -Force
    }
    Get-ChildItem -LiteralPath $packageRoot -Directory -Recurse -Force |
        Where-Object { $_.Name -in @('bin','obj') } |
        Sort-Object FullName -Descending |
        Remove-Item -Recurse -Force
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    if(Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    "SOURCE PACK: $zip`r`nSHA256: $hash" | Set-Content -LiteralPath (Join-Path $dist ("SMART_PLAYOUT_v${safeVersion}_SOURCE_PACK_SHA256.txt")) -Encoding UTF8
    Write-Host "Source package ready: $zip"
    Write-Host "SHA256: $hash"
}
finally {
    if(Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
