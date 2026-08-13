# Check for admin rights
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process pwsh -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

Write-Host "Fetching https://localhost:8081/_explorer/emulator.pem..."
Invoke-WebRequest -Uri https://localhost:8081/_explorer/emulator.pem -OutFile emulatorcert.crt -SkipCertificateCheck -Method Get   
Write-Host "Importing this cert into personal cert store"
$output = Import-Certificate -FilePath .\emulatorcert.crt -CertStoreLocation "Cert:\LocalMachine\My"
Write-Host "Importing this cert into root cert store"
$output = Import-Certificate -FilePath .\emulatorcert.crt -CertStoreLocation "Cert:\LocalMachine\Root"
Write-Host "Cleanup."
Remove-Item emulatorcert.crt -Force

Read-Host "Press Enter to continue"
