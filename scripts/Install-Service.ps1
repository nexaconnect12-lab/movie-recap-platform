#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$ConnectionString,
    [string]$Name = 'MovieRecap.Processing',
    [string]$LogDirectory = "$env:ProgramData\MovieRecap\Logs"
)
$ErrorActionPreference='Stop'
$resolved=(Resolve-Path -LiteralPath $Executable).Path
if (Get-Service -Name $Name -ErrorAction SilentlyContinue) { throw "Service $Name already exists; no changes made." }
# Dedicated virtual service identity: no interactive account or service password.
New-Service -Name $Name -DisplayName 'Movie Recap Processing Worker' -BinaryPathName ('"' + $resolved + '"') -StartupType Automatic | Out-Null
try {
    & sc.exe config $Name obj= "NT SERVICE\$Name" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set virtual service identity.' }
    & sc.exe failure $Name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not configure recovery.' }
    # Restrict the service key; database secret is injected into process environment, never into appsettings/source.
    $keyPath="HKLM:\SYSTEM\CurrentControlSet\Services\$Name"
    $acl=Get-Acl -LiteralPath $keyPath
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($identity in @('SYSTEM','BUILTIN\Administrators')) {
        $rule=[System.Security.AccessControl.RegistryAccessRule]::new($identity,'FullControl','ContainerInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $keyPath -AclObject $acl
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
    & icacls.exe $LogDirectory /grant "NT SERVICE\${Name}:(OI)(CI)M" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant log directory access.' }
    New-ItemProperty -LiteralPath $keyPath -Name Environment -PropertyType MultiString -Value @("ConnectionStrings__Platform=$ConnectionString", "Logging__FilePath=$(Join-Path $LogDirectory 'worker-.json')") | Out-Null
    & icacls.exe (Split-Path $resolved) /grant "NT SERVICE\${Name}:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant executable directory access.' }
    Write-Host "Installed $Name. Configure restricted log/media folders before starting."
} catch { Write-Error "Installation is incomplete: $_. Inspect/remove the service with Uninstall-Service.ps1."; throw }

