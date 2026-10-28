<#
.SYNOPSIS
    Signs an unsigned SidePhase Mail beta/development MSIX with a certificate created on THIS PC and installs it.

.DESCRIPTION
    CI and beta builds of the MSIX are unsigned: no signing key is stored in the repository or in CI. Windows only
    installs signed packages, so for testing this script:

      1. Creates (once) a self-signed code-signing certificate in your user certificate store whose subject matches
         the package's Publisher ("CN=SidePhase Studio (development)"). The private key never leaves this PC.
      2. Trusts that certificate for installing packages on this PC (LocalMachine\TrustedPeople - needs admin; you
         are asked to confirm).
      3. Signs the package with it (signtool from the Windows SDK).
      4. Installs the Windows App Runtime framework package from the Dependencies folder if it's there, then
         installs (or updates) SidePhase Mail.

    This is for development and beta testing only. Release builds are signed by the Microsoft Store (Store builds) or
    with SidePhase Studio's real code-signing certificate, which is never committed. Remove the test certificate
    with -RemoveCertificate when you're done.

.EXAMPLE
    .\Install-DevPackage.ps1 -Package .\SidePhaseMail_0.6.0.0_x64.msix
.EXAMPLE
    .\Install-DevPackage.ps1 -RemoveCertificate
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string] $Package,

    [Parameter(ParameterSetName = 'Remove')]
    [switch] $RemoveCertificate
)

$ErrorActionPreference = 'Stop'
$Subject = 'CN=SidePhase Studio (development)'
$FriendlyName = 'SidePhase Mail local test signing (this PC only)'

function Get-TestCertificate {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.FriendlyName -eq $FriendlyName } | Select-Object -First 1
}

function Test-Admin {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($RemoveCertificate) {
    $cert = Get-TestCertificate
    if (-not $cert) { Write-Host 'No SidePhase Mail test certificate found.'; return }
    if (-not (Test-Admin)) { throw 'Run PowerShell as administrator to remove the trusted certificate.' }
    Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint | Remove-Item
    Remove-Item $cert.PSPath
    Write-Host 'Removed the SidePhase Mail test certificate.'
    return
}

$Package = (Resolve-Path $Package).Path
if (-not (Test-Admin)) { throw 'Run PowerShell as administrator: trusting the test certificate needs it.' }

# 1. Certificate (created once, kept in your user store; private key not exportable).
$cert = Get-TestCertificate
if (-not $cert) {
    Write-Host "Creating a test signing certificate for this PC ($Subject)..."
    $cert = New-SelfSignedCertificate -Type Custom -Subject $Subject -FriendlyName $FriendlyName `
        -KeyUsage DigitalSignature -KeyExportPolicy NonExportable -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(1)
}

# 2. Trust it for package installation on this PC only.
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $cert.Thumbprint)) {
    $answer = Read-Host "Trust the test certificate $($cert.Thumbprint) for installing packages on this PC? (y/n)"
    if ($answer -ne 'y') { throw 'Cancelled.' }
    $store = New-Object Security.Cryptography.X509Certificates.X509Store 'TrustedPeople', 'LocalMachine'
    $store.Open('ReadWrite'); $store.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData)); $store.Close()
}

# 3. Sign.
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK (it comes with Visual Studio''s Windows App SDK workload).' }
& $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /s My $Package
if ($LASTEXITCODE -ne 0) { throw "Signing failed (signtool exit code $LASTEXITCODE)." }

# 4. Install dependencies (Windows App Runtime) and the app.
$deps = Join-Path (Split-Path $Package) 'Dependencies\x64'
$install = @{ Path = $Package; ForceUpdateFromAnyVersion = $true }
if (Test-Path $deps) {
    $dependencyPackages = @(Get-ChildItem $deps -Filter *.msix | ForEach-Object FullName)
    if ($dependencyPackages.Count -gt 0) { $install.DependencyPath = $dependencyPackages }
}
Add-AppxPackage @install
Write-Host 'SidePhase Mail is installed. Find it in the Start menu.'
