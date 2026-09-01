param(
    [string]$KspDir = 'C:\Program Files (x86)\Steam\steamapps\common\Kerbal Space Program',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$managedDir = Join-Path $KspDir 'KSP_x64_Data\Managed'
$requiredFiles = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'Assembly-CSharp.dll',
    'UnityEngine.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll',
    'UnityEngine.InputLegacyModule.dll'
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $managedDir $file))) {
        throw "Required KSP assembly missing: $(Join-Path $managedDir $file)"
    }
}

$sdkLine = dotnet --list-sdks | Select-Object -Last 1
if (-not $sdkLine) { throw 'No .NET SDK found. Install an SDK that includes the Roslyn C# compiler.' }
$sdkVersion = ($sdkLine -split ' ')[0]
$dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
$compiler = Join-Path $dotnetRoot "sdk\$sdkVersion\Roslyn\bincore\csc.dll"
if (-not (Test-Path -LiteralPath $compiler)) { throw "Roslyn compiler not found: $compiler" }

$outputDir = Join-Path $PSScriptRoot "bin\$Configuration"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$outputDll = Join-Path $outputDir 'KspRenderProbe.dll'
$source = Join-Path $PSScriptRoot 'Source\KspRenderProbe.cs'

$references = $requiredFiles | ForEach-Object { '/r:' + (Join-Path $managedDir $_) }
& dotnet $compiler /noconfig /nostdlib /target:library /out:$outputDll $references $source
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

Get-Item -LiteralPath $outputDll | Select-Object FullName, Length, LastWriteTime
