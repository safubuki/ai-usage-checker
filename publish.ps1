$ErrorActionPreference = 'Stop'

try {
    $projectPath = Join-Path $PSScriptRoot 'AIUsageChecker.csproj'
    $publishDirectory = Join-Path $PSScriptRoot 'bin\Release\publish'
    $publishedExe = [System.IO.Path]::GetFullPath((Join-Path $publishDirectory 'AIUsageChecker.exe'))

    $runningApps = @(Get-Process -Name AIUsageChecker -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and [string]::Equals(
            [System.IO.Path]::GetFullPath($_.Path),
            $publishedExe,
            [System.StringComparison]::OrdinalIgnoreCase)
    })

    foreach ($runningApp in $runningApps) {
        if ($runningApp.HasExited) { continue }

        Write-Host "Closing the app being updated (PID $($runningApp.Id))..."
        if (-not $runningApp.CloseMainWindow()) {
            if ($runningApp.HasExited) { continue }
            throw 'Please close the app at bin\Release\publish\AIUsageChecker.exe and run publish.bat again.'
        }
        if (-not $runningApp.WaitForExit(10000)) {
            throw 'The app did not exit within 10 seconds. Please close it and run publish.bat again.'
        }
    }

    Write-Host 'Building single-file release package...'
    & dotnet publish $projectPath -c Release -r win-x64 --self-contained false -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    Write-Host ''
    Write-Host '[SUCCESS] Publish completed!'
    Write-Host "Executable: $publishedExe"
    exit 0
}
catch {
    Write-Host "[ERROR] $($_.Exception.Message)"
    exit 1
}
