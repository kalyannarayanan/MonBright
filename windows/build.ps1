<#
    build.ps1 - build MonBright.exe

    NOTE: keep this file pure ASCII. PowerShell 5.1 reads a .ps1 with no BOM
    using the ANSI codepage, where the third byte of a UTF-8 em dash decodes to
    a curly closing quote - which PowerShell honours as a string delimiter.

    Deliberately has no dependencies. It uses the C# compiler that ships inside
    Windows itself (C:\Windows\Microsoft.NET\Framework64\...\csc.exe), so there
    is nothing to install: no Visual Studio, no .NET SDK, no NuGet.

    Output: bin\MonBright.exe - one self-contained file, ~100 KB, that runs on
    any Windows 10 (1903+) or Windows 11 machine with no runtime download.

        .\build.ps1            build
        .\build.ps1 -Run       build, then start it
        .\build.ps1 -Zip       build, then produce dist\MonBright-<ver>.zip
#>

[CmdletBinding()]
param(
    [switch]$Run,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

$AppName = 'MonBright'
$Version = '1.0.0'
$OutDir  = Join-Path $PSScriptRoot 'bin'
$OutExe  = Join-Path $OutDir "$AppName.exe"
$IcoPath = Join-Path $OutDir "$AppName.ico"
$PngPath = Join-Path $PSScriptRoot 'assets\icon.png'

# ---------------------------------------------------------------------------
# Locate the in-box compiler
# ---------------------------------------------------------------------------

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "No in-box C# compiler found. Install the .NET Framework 4.x developer files, or build with the .NET SDK."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ---------------------------------------------------------------------------
# Build a multi-resolution .ico from assets\icon.png
#
# System.Drawing can only *read* .ico files, so the container is assembled by
# hand: an ICONDIR header, one 16-byte ICONDIRENTRY per size, then the PNG
# payloads. Vista and newer accept PNG-compressed entries at every size.
# ---------------------------------------------------------------------------

function New-MultiSizeIcon {
    param(
        [Parameter(Mandatory)][string]$SourcePng,
        [Parameter(Mandatory)][string]$Destination,
        [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    )

    Add-Type -AssemblyName System.Drawing

    $src = [System.Drawing.Image]::FromFile($SourcePng)
    $frames = @()
    try {
        foreach ($s in $Sizes) {
            $bmp = New-Object System.Drawing.Bitmap($s, $s)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            try {
                $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
                $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $g.Clear([System.Drawing.Color]::Transparent)
                $rect = New-Object System.Drawing.Rectangle -ArgumentList 0, 0, $s, $s
                $g.DrawImage($src, $rect)
            } finally { $g.Dispose() }

            $ms = New-Object System.IO.MemoryStream
            try {
                $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames += , @{ Size = $s; Data = $ms.ToArray() }
            } finally { $ms.Dispose(); $bmp.Dispose() }
        }
    } finally { $src.Dispose() }

    $fs = [System.IO.File]::Create($Destination)
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        $bw.Write([UInt16]0)              # reserved
        $bw.Write([UInt16]1)              # type: icon
        $bw.Write([UInt16]$frames.Count)

        $offset = 6 + (16 * $frames.Count)
        foreach ($f in $frames) {
            $dim = $f.Size
            if ($dim -ge 256) { $dim = 0 }   # 0 means 256 in the ICO header
            $bw.Write([Byte]$dim)            # width
            $bw.Write([Byte]$dim)            # height
            $bw.Write([Byte]0)               # palette entries
            $bw.Write([Byte]0)               # reserved
            $bw.Write([UInt16]1)             # colour planes
            $bw.Write([UInt16]32)            # bits per pixel
            $bw.Write([UInt32]$f.Data.Length)
            $bw.Write([UInt32]$offset)
            $offset += $f.Data.Length
        }
        foreach ($f in $frames) { $bw.Write($f.Data) }
    } finally { $bw.Dispose(); $fs.Dispose() }
}

$needIcon = -not (Test-Path $IcoPath)
if (-not $needIcon -and (Test-Path $PngPath)) {
    if ((Get-Item $PngPath).LastWriteTimeUtc -gt (Get-Item $IcoPath).LastWriteTimeUtc) { $needIcon = $true }
}
if ($needIcon) {
    if (Test-Path $PngPath) {
        Write-Host "Building app icon..." -ForegroundColor Cyan
        New-MultiSizeIcon -SourcePng $PngPath -Destination $IcoPath
    } else {
        Write-Warning "assets\icon.png not found - building without an app icon."
        $IcoPath = $null
    }
}

# ---------------------------------------------------------------------------
# Compile
# ---------------------------------------------------------------------------

# A running instance holds an exclusive lock on its own exe, so csc would fail
# with CS0016. Only stop a process that is actually running the file we are
# about to overwrite - never someone else's identically named program.
Get-Process -Name $AppName -ErrorAction SilentlyContinue | ForEach-Object {
    $running = $null
    try { $running = $_.MainModule.FileName } catch { }
    if ($running -and ($running -eq $OutExe)) {
        Write-Host "Stopping running instance (pid $($_.Id))..." -ForegroundColor DarkGray
        $_ | Stop-Process -Force
        Start-Sleep -Milliseconds 300
    }
}

Write-Host "Compiling $AppName $Version..." -ForegroundColor Cyan

$sources = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src') -Filter *.cs -Recurse |
           ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { throw "No source files found in src\." }

$cscArgs = @(
    '/nologo'
    '/noconfig'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/utf8output'
    # Without this the compiler decodes sources using the machine's ANSI
    # codepage, so non-ASCII string literals would mangle on a non-Western
    # Windows install.
    '/codepage:65001'
    "/out:$OutExe"
    "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')"
    '/r:mscorlib.dll'
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Management.dll'
)
if ($IcoPath) { $cscArgs += "/win32icon:$IcoPath" }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }

$size = [Math]::Round((Get-Item $OutExe).Length / 1KB, 1)
Write-Host "Built $OutExe ($size KB)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# Optional packaging / launch
# ---------------------------------------------------------------------------

if ($Zip) {
    $distDir = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Force -Path $distDir | Out-Null
    $stage = Join-Path $distDir 'stage'
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    Copy-Item $OutExe $stage
    foreach ($doc in @('README.md', 'LICENSE')) {
        $p = Join-Path $PSScriptRoot $doc
        if (Test-Path $p) { Copy-Item $p $stage }
    }

    $zipPath = Join-Path $distDir "$AppName-$Version.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
    Remove-Item $stage -Recurse -Force
    Write-Host "Packaged $zipPath" -ForegroundColor Green
}

if ($Run) {
    Get-Process -Name $AppName -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process $OutExe
    Write-Host "Started. Look for the monitor icon in your notification area." -ForegroundColor Green
}
