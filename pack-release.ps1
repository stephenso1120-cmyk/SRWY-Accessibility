$stagingDir = "C:\Users\steph\Documents\SRWY\SRWY\Accessibility-Mod-Template\release-staging"
$zipPath = "C:\Users\steph\Documents\SRWY\SRWY\Accessibility-Mod-Template\SRWYAccess-v2.57.zip"

# Clean staging
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stagingDir "Mods") | Out-Null

# Copy files
Copy-Item "C:\Program Files (x86)\Steam\steamapps\common\SRWY\Tolk.dll" $stagingDir
Copy-Item "C:\Program Files (x86)\Steam\steamapps\common\SRWY\nvdaControllerClient64.dll" $stagingDir
Copy-Item "C:\Program Files (x86)\Steam\steamapps\common\SRWY\SRWYSafe.dll" $stagingDir
Copy-Item "C:\Users\steph\Documents\SRWY\SRWY\Accessibility-Mod-Template\src\bin\Release\SRWYAccess.dll" (Join-Path $stagingDir "Mods")
Copy-Item "C:\Users\steph\Documents\SRWY\SRWY\Accessibility-Mod-Template\README.txt" $stagingDir

# Create zip
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $zipPath

# Verify
Write-Host "=== Release contents ==="
Get-ChildItem $stagingDir -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($stagingDir.Length + 1)
    if ($_.PSIsContainer) {
        Write-Host "  [DIR] $rel"
    } else {
        Write-Host "  $rel ($($_.Length) bytes)"
    }
}
Write-Host ""
$zipSize = (Get-Item $zipPath).Length
Write-Host "ZIP created: $zipPath ($zipSize bytes)"

# Clean up staging
Remove-Item $stagingDir -Recurse -Force
