param(
    [switch]$SkipRuntimeBuild,
    [switch]$SkipInstallerCompile,
    [string]$InnoSetupCompiler
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$installerRoot = Join-Path $projectRoot "artifacts\installer"
$payloadRoot = Join-Path $installerRoot "payload"
$pythonRoot = Join-Path $payloadRoot "python"
$backendRoot = Join-Path $payloadRoot "backend"
$workerStage = Join-Path $installerRoot "worker-publish"
$pythonZip = Join-Path $installerRoot "python-embed.zip"
$getPip = Join-Path $installerRoot "get-pip.py"
$pythonVersion = "3.12.10"
$pythonArchiveUrl = "https://www.python.org/ftp/python/$pythonVersion/python-$pythonVersion-embed-amd64.zip"
$pythonExecutable = Join-Path $pythonRoot "python.exe"

New-Item -ItemType Directory -Force -Path $installerRoot, $payloadRoot, $backendRoot | Out-Null

if (-not $SkipRuntimeBuild) {
    if (Test-Path $pythonRoot) {
        Remove-Item -LiteralPath $pythonRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $pythonRoot | Out-Null

    Write-Host "Downloading the pinned CPython embeddable runtime..."
    Invoke-WebRequest -Uri $pythonArchiveUrl -OutFile $pythonZip
    Expand-Archive -LiteralPath $pythonZip -DestinationPath $pythonRoot -Force

    $pthPath = Join-Path $pythonRoot "python312._pth"
    if (-not (Test-Path $pthPath)) {
        throw "Expected embedded Python path configuration was not found: $pthPath"
    }
    $pthContent = Get-Content -LiteralPath $pthPath
    $pthContent = @($pthContent | Where-Object { $_ -ne "#import site" })
    $pthContent += @("Lib\site-packages", "import site")
    Set-Content -LiteralPath $pthPath -Value $pthContent -Encoding ascii
    New-Item -ItemType Directory -Force -Path (Join-Path $pythonRoot "Lib\site-packages") | Out-Null

    Write-Host "Bootstrapping pip in the private runtime..."
    Invoke-WebRequest -Uri "https://bootstrap.pypa.io/get-pip.py" -OutFile $getPip
    & $pythonExecutable $getPip --no-warn-script-location
    if ($LASTEXITCODE -ne 0) {
        throw "pip bootstrap failed with exit code $LASTEXITCODE."
    }

}

if (-not (Test-Path $pythonExecutable)) {
    throw "Private Python runtime was not found: $pythonExecutable. Remove -SkipRuntimeBuild or provide the completed runtime."
}

Write-Host "Installing the pinned CPU-only Demucs environment (large download)..."
& $pythonExecutable -m pip install --disable-pip-version-check --no-warn-script-location -r (Join-Path $projectRoot "backend\requirements.txt")
if ($LASTEXITCODE -ne 0) {
    throw "Private Demucs runtime installation failed with exit code $LASTEXITCODE."
}

& $pythonExecutable -c "from importlib.metadata import version; import demucs.separate, torch; assert torch.__version__ == '2.5.1+cpu'; print('Demucs', version('demucs'), 'Torch', torch.__version__)"
if ($LASTEXITCODE -ne 0) {
    throw "The packaged Python runtime failed its import/version check."
}

Copy-Item -LiteralPath (Join-Path $projectRoot "backend\demucs_worker.py") -Destination $backendRoot -Force

Write-Host "Publishing the .NET 8 WPF application..."
dotnet publish (Join-Path $projectRoot "src\StemStudio.App\StemStudio.App.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $payloadRoot
if ($LASTEXITCODE -ne 0) {
    throw "Application publish failed with exit code $LASTEXITCODE."
}

if (Test-Path $workerStage) {
    Remove-Item -LiteralPath $workerStage -Recurse -Force
}
Write-Host "Publishing the standalone worker..."
dotnet publish (Join-Path $projectRoot "src\StemStudio.Worker\StemStudio.Worker.csproj") `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $workerStage
if ($LASTEXITCODE -ne 0) {
    throw "Worker publish failed with exit code $LASTEXITCODE."
}
Copy-Item -LiteralPath (Join-Path $workerStage "StemStudio.Worker.exe") -Destination $payloadRoot -Force
Remove-Item -LiteralPath $workerStage -Recurse -Force

if ($SkipInstallerCompile) {
    Write-Host "Installer payload prepared at $payloadRoot (installer compilation skipped by request)."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($InnoSetupCompiler)) {
    $compilerCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($compilerCommand) {
        $InnoSetupCompiler = $compilerCommand.Source
    }
    else {
        $knownCompilers = @(
            (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
            (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
            (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
        )
        foreach ($knownCompiler in $knownCompilers) {
            if (Test-Path $knownCompiler) {
                $InnoSetupCompiler = $knownCompiler
                break
            }
        }
    }
}

if (-not $InnoSetupCompiler -or -not (Test-Path $InnoSetupCompiler)) {
    throw "Payload is ready, but Inno Setup 6 was not found. Install Inno Setup 6, then rerun with -InnoSetupCompiler <path-to-ISCC.exe>."
}

Write-Host "Compiling the per-user Windows installer..."
& $InnoSetupCompiler (Join-Path $PSScriptRoot "StemStudio.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Installer created at $(Join-Path $installerRoot 'StemStudio-Setup.exe')"
