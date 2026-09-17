#Requires -Version 5.1

[CmdletBinding()]
param(
    [switch] $SkipElevation
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    if ($SkipElevation) {
        throw 'The Team Hub hosting installer must run as Administrator.'
    }

    Write-Host 'Administrator access is required. Approve the Windows prompt to continue.' -ForegroundColor Yellow
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -SkipElevation"
    $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$logPath = Join-Path $env:TEMP "TeamHub-Hosting-$timestamp.log"
Start-Transcript -Path $logPath -Force | Out-Null

function Write-Step([string] $message) {
    Write-Host ''
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Read-Required([string] $prompt) {
    do {
        $value = (Read-Host $prompt).Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            Write-Host 'A value is required.' -ForegroundColor Yellow
        }
    } while ([string]::IsNullOrWhiteSpace($value))
    return $value
}

function Read-Default([string] $prompt, [string] $defaultValue) {
    $value = Read-Host "$prompt [$defaultValue]"
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $defaultValue
    }
    return $value.Trim()
}

function Read-YesNo([string] $prompt, [bool] $defaultYes = $true) {
    $suffix = if ($defaultYes) { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        $answer = (Read-Host "$prompt $suffix").Trim()
        if ([string]::IsNullOrWhiteSpace($answer)) {
            return $defaultYes
        }
        if ($answer -match '^(y|yes)$') { return $true }
        if ($answer -match '^(n|no)$') { return $false }
        Write-Host 'Enter Y or N.' -ForegroundColor Yellow
    }
}

function Read-Port([string] $prompt, [int] $defaultValue) {
    while ($true) {
        $text = Read-Default $prompt $defaultValue.ToString()
        $port = 0
        if ([int]::TryParse($text, [ref]$port) -and $port -ge 1 -and $port -le 65535) {
            return $port
        }
        Write-Host 'Enter a TCP port between 1 and 65535.' -ForegroundColor Yellow
    }
}

function Convert-SecureStringToPlainText([Security.SecureString] $secureValue) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureValue)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Resolve-FullPath([string] $path) {
    return [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($path))
}

function Assert-SafeInstallPath([string] $path) {
    $fullPath = Resolve-FullPath $path
    $root = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
    $candidate = $fullPath.TrimEnd('\')
    if ($candidate -eq $root -or $candidate.Length -le ($root.Length + 3)) {
        throw "The installation path '$fullPath' is too broad. Select a dedicated Team Hub directory."
    }

    $forbidden = @($env:SystemRoot, $env:ProgramFiles, $env:USERPROFILE) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { (Resolve-FullPath $_).TrimEnd('\') }
    if ($forbidden -contains $candidate) {
        throw "The installation path '$fullPath' is a protected system location. Select a dedicated subdirectory."
    }
    return $fullPath
}

function Test-DirectoryHasContent([string] $path) {
    return (Test-Path -LiteralPath $path -PathType Container) -and
        $null -ne (Get-ChildItem -LiteralPath $path -Force | Select-Object -First 1)
}

function Copy-DirectoryContents([string] $source, [string] $destination) {
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        throw "Source directory was not found: $source"
    }
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & robocopy.exe $source $destination /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Copy failed from '$source' to '$destination' (robocopy exit code $LASTEXITCODE)."
    }
}

function Ensure-IisInstalled {
    Write-Step 'Checking IIS'
    if (Get-Command Install-WindowsFeature -ErrorAction SilentlyContinue) {
        $feature = Get-WindowsFeature -Name Web-Server
        if (-not $feature.Installed) {
            Write-Host 'Installing the IIS Web Server role and management tools...'
            $result = Install-WindowsFeature -Name Web-Server -IncludeManagementTools
            if (-not $result.Success) {
                throw 'Windows could not install the IIS Web Server role.'
            }
        }
    }
    elseif (Get-Command Enable-WindowsOptionalFeature -ErrorAction SilentlyContinue) {
        $iis = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole
        if ($iis.State -ne 'Enabled') {
            Write-Host 'Enabling IIS and the IIS management console...'
            Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -All -NoRestart | Out-Null
            Enable-WindowsOptionalFeature -Online -FeatureName IIS-ManagementConsole -All -NoRestart | Out-Null
        }
    }
    else {
        throw 'This machine does not provide Windows IIS installation commands.'
    }

    Import-Module WebAdministration -ErrorAction Stop
}

function Test-AspNetCoreHostingModule {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $modulePaths = @(
        (Join-Path $env:ProgramFiles 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll')
    )
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $modulePaths += Join-Path $programFilesX86 'IIS\Asp.Net Core Module\V2\aspnetcorev2.dll'
    }
    return $null -ne ($modulePaths | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1)
}

function Test-DotNet10AspNetRuntime {
    if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
        return $false
    }
    $runtimes = & dotnet.exe --list-runtimes 2>$null
    return $null -ne ($runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' } | Select-Object -First 1)
}

function Ensure-HostingBundleInstalled {
    Write-Step 'Checking the ASP.NET Core Hosting Bundle'
    if ((Test-AspNetCoreHostingModule) -and (Test-DotNet10AspNetRuntime)) {
        Write-Host 'ASP.NET Core Module V2 and the .NET 10 ASP.NET Core runtime are installed.' -ForegroundColor Green
        return
    }

    Write-Host 'The ASP.NET Core Hosting Bundle is not installed.' -ForegroundColor Yellow
    Write-Host 'Download the .NET 10 Hosting Bundle from Microsoft, then provide its local file path.'
    $installerPath = Resolve-FullPath (Read-Required 'Path to the .NET 10 Hosting Bundle installer (.exe)')
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        throw "Hosting Bundle installer was not found: $installerPath"
    }

    Write-Host 'Installing the Hosting Bundle...'
    $installerArguments = @{
        FilePath = $installerPath
        ArgumentList = '/install /quiet /norestart'
        WindowStyle = 'Hidden'
        Wait = $true
        PassThru = $true
    }
    $installer = Start-Process @installerArguments
    if ($installer.ExitCode -notin @(0, 3010)) {
        throw "Hosting Bundle installation failed with exit code $($installer.ExitCode)."
    }

    if (Read-YesNo 'Restart IIS services now? This briefly stops other IIS websites on this server.' $true) {
        Stop-Service -Name WAS -Force -ErrorAction SilentlyContinue
        Start-Service -Name W3SVC
    }
    else {
        Write-Host 'Restart Windows or the IIS services before using Team Hub.' -ForegroundColor Yellow
    }

    if (-not (Test-AspNetCoreHostingModule) -or -not (Test-DotNet10AspNetRuntime)) {
        throw 'ASP.NET Core Module V2 or the .NET 10 runtime is still unavailable. Restart Windows, then run this installer again.'
    }
}

function Set-AppPoolEnvironmentVariable(
    [string] $appPoolName,
    [string] $variableName,
    [string] $variableValue) {
    $filter = "system.applicationHost/applicationPools/add[@name='$appPoolName']/environmentVariables"
    try {
        Remove-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -AtElement @{ name = $variableName } -ErrorAction SilentlyContinue
    }
    catch {
        # The value does not exist yet.
    }

    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter -Name '.' -Value @{ name = $variableName; value = $variableValue } | Out-Null
}

function Grant-TeamHubPermissions([string] $installPath, [string] $appPoolName) {
    Write-Step 'Configuring application folder permissions'
    $identity = "IIS AppPool\$appPoolName"
    & icacls.exe $installPath /grant "$($identity):(OI)(CI)RX" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant read access to $identity."
    }

    $dataPath = Join-Path $installPath 'data'
    & icacls.exe $dataPath /grant "$($identity):(OI)(CI)M" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Could not grant modify access to '$dataPath'."
    }
}

function Get-TeamHubPackage([string] $inputPath) {
    $resolved = Resolve-FullPath $inputPath
    $projectPath = Join-Path $resolved 'src\TeamHub.Web\TeamHub.Web.csproj'
    if (Test-Path -LiteralPath $projectPath -PathType Leaf) {
        if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
            throw 'The .NET SDK is required to publish from source. Provide a pre-published Team Hub folder instead.'
        }
        $installedSdks = @(& dotnet.exe --list-sdks 2>$null)
        if ($null -eq ($installedSdks | Where-Object { $_ -match '^10\.' } | Select-Object -First 1)) {
            throw 'The .NET 10 SDK is required to publish from source. Install it or provide a pre-published Team Hub folder instead.'
        }

        Write-Step 'Publishing Team Hub in Release mode'
        $temporaryPath = Join-Path $env:TEMP "TeamHub-Publish-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $temporaryPath -Force | Out-Null
        & dotnet.exe publish $projectPath --configuration Release --output $temporaryPath
        if ($LASTEXITCODE -ne 0) {
            throw 'dotnet publish failed. Review the errors shown above.'
        }

        return [pscustomobject]@{
            PackagePath = $temporaryPath
            TemporaryPath = $temporaryPath
            SourceRoot = $resolved
        }
    }

    if (Test-Path -LiteralPath (Join-Path $resolved 'TeamHub.Web.dll') -PathType Leaf) {
        return [pscustomobject]@{
            PackagePath = $resolved
            TemporaryPath = $null
            SourceRoot = $resolved
        }
    }

    throw "The path '$resolved' is neither a Team Hub source repository nor a published Team Hub folder."
}

function Backup-TeamHubDeployment([string] $installPath) {
    $dataPath = Join-Path $installPath 'data'
    $configPath = Join-Path $installPath 'config'
    if (-not (Test-DirectoryHasContent $dataPath) -and -not (Test-DirectoryHasContent $configPath)) {
        return $null
    }

    $parent = Split-Path -Parent $installPath
    $leaf = Split-Path -Leaf $installPath
    $backupPath = Join-Path $parent "$leaf-backups\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Write-Step "Backing up the existing deployment to $backupPath"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    if (Test-Path -LiteralPath $dataPath) {
        Copy-DirectoryContents $dataPath (Join-Path $backupPath 'data')
    }
    if (Test-Path -LiteralPath $configPath) {
        Copy-DirectoryContents $configPath (Join-Path $backupPath 'config')
    }
    return $backupPath
}

function Copy-TeamHubApplication([string] $packagePath, [string] $installPath) {
    Write-Step 'Deploying Team Hub application files'
    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    $excludedData = Join-Path $packagePath 'data'
    $excludedConfig = Join-Path $packagePath 'config'
    & robocopy.exe $packagePath $installPath /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /NFL /NDL /NP /XD $excludedData $excludedConfig | Out-Null
    if ($LASTEXITCODE -gt 7) {
        throw "Application deployment failed (robocopy exit code $LASTEXITCODE)."
    }

    if (-not (Test-Path -LiteralPath (Join-Path $installPath 'TeamHub.Web.dll'))) {
        throw 'The deployed application does not contain TeamHub.Web.dll.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installPath 'web.config'))) {
        throw 'The deployed application does not contain the IIS web.config generated by dotnet publish.'
    }
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Initialize-TeamHubAdministrator(
    [string] $installPath,
    [string] $userId,
    [string] $displayName,
    [Security.SecureString] $password) {
    Write-Step 'Creating the first Team Hub administrator'
    $plainPassword = Convert-SecureStringToPlainText $password
    if ($plainPassword.Length -lt 8) {
        throw 'The bootstrap administrator password must contain at least 8 characters.'
    }

    $port = Get-FreeTcpPort
    $applicationExe = Join-Path $installPath 'TeamHub.Web.exe'
    $applicationDll = Join-Path $installPath 'TeamHub.Web.dll'
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    if (Test-Path -LiteralPath $applicationExe) {
        $startInfo.FileName = $applicationExe
    }
    else {
        $startInfo.FileName = 'dotnet.exe'
        $startInfo.Arguments = '"' + $applicationDll + '"'
    }
    $startInfo.WorkingDirectory = $installPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $startInfo.EnvironmentVariables['ASPNETCORE_URLS'] = "http://127.0.0.1:$port"
    $startInfo.EnvironmentVariables['TEAMHUB_BOOTSTRAP_ADMIN_USER'] = $userId
    $startInfo.EnvironmentVariables['TEAMHUB_BOOTSTRAP_ADMIN_NAME'] = $displayName
    $startInfo.EnvironmentVariables['TEAMHUB_BOOTSTRAP_ADMIN_PASSWORD'] = $plainPassword

    $applicationProcess = $null
    try {
        $applicationProcess = [Diagnostics.Process]::Start($startInfo)
        $ready = $false
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            if ($applicationProcess.HasExited) {
                throw "Team Hub exited during administrator initialization with code $($applicationProcess.ExitCode)."
            }
            try {
                Invoke-WebRequest -Uri "http://127.0.0.1:$port/Login" -UseBasicParsing -TimeoutSec 3 | Out-Null
                $ready = $true
                break
            }
            catch {
                Start-Sleep -Seconds 1
            }
        }
        if (-not $ready) {
            throw 'Team Hub did not finish first-start initialization within 90 seconds.'
        }
        Write-Host "Bootstrap administrator created: $userId" -ForegroundColor Green
    }
    finally {
        $plainPassword = $null
        if ($null -ne $applicationProcess -and -not $applicationProcess.HasExited) {
            $applicationProcess.Kill()
            $applicationProcess.WaitForExit()
        }
    }
}

function Configure-TeamHubIisSite(
    [string] $siteName,
    [string] $appPoolName,
    [string] $installPath,
    [string] $hostName,
    [int] $httpPort,
    [bool] $useHttps,
    [int] $httpsPort,
    [string] $certificateThumbprint) {
    Write-Step 'Configuring the IIS application pool and website'
    if (-not (Test-Path "IIS:\AppPools\$appPoolName")) {
        New-WebAppPool -Name $appPoolName | Out-Null
    }
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name managedPipelineMode -Value 'Integrated'
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty "IIS:\AppPools\$appPoolName" -Name processModel.maxProcesses -Value 1

    if (-not (Test-Path "IIS:\Sites\$siteName")) {
        New-Website -Name $siteName -PhysicalPath $installPath -ApplicationPool $appPoolName -Port $httpPort -HostHeader $hostName | Out-Null
    }
    else {
        Stop-Website -Name $siteName -ErrorAction SilentlyContinue
        Set-ItemProperty "IIS:\Sites\$siteName" -Name physicalPath -Value $installPath
        Set-ItemProperty "IIS:\Sites\$siteName" -Name applicationPool -Value $appPoolName
        $httpBinding = Get-WebBinding -Name $siteName -Protocol 'http' |
            Where-Object { $_.bindingInformation -eq "*:$($httpPort):$hostName" } |
            Select-Object -First 1
        if ($null -eq $httpBinding) {
            New-WebBinding -Name $siteName -Protocol 'http' -IPAddress '*' -Port $httpPort -HostHeader $hostName
        }
    }

    if ($useHttps) {
        $certificate = Get-Item "Cert:\LocalMachine\My\$certificateThumbprint" -ErrorAction SilentlyContinue
        if ($null -eq $certificate -or -not $certificate.HasPrivateKey) {
            throw "The selected LocalMachine certificate '$certificateThumbprint' was not found or has no private key."
        }
        if ($certificate.NotAfter -le (Get-Date)) {
            throw "The selected certificate expired on $($certificate.NotAfter)."
        }

        $httpsBinding = Get-WebBinding -Name $siteName -Protocol 'https' |
            Where-Object { $_.bindingInformation -eq "*:$($httpsPort):$hostName" } |
            Select-Object -First 1
        if ($null -eq $httpsBinding) {
            New-WebBinding -Name $siteName -Protocol 'https' -IPAddress '*' -Port $httpsPort -HostHeader $hostName -SslFlags 1
        }

        $sslBindingPath = "IIS:\SslBindings\0.0.0.0!$($httpsPort)!$hostName"
        if (Test-Path $sslBindingPath) {
            Set-Item -Path $sslBindingPath -Thumbprint $certificate.Thumbprint -SSLFlags 1
        }
        else {
            New-Item -Path $sslBindingPath -Thumbprint $certificate.Thumbprint -SSLFlags 1 | Out-Null
        }

        Remove-WebBinding -Name $siteName -Protocol 'http' -IPAddress '*' -Port $httpPort -HostHeader $hostName -ErrorAction SilentlyContinue
    }

    Set-AppPoolEnvironmentVariable $appPoolName 'ASPNETCORE_ENVIRONMENT' 'Production'
    Set-AppPoolEnvironmentVariable $appPoolName 'AllowedHosts' $hostName
}

function Configure-TeamHubFirewall([string] $siteName, [int] $port) {
    if (-not (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)) {
        Write-Host 'Windows Firewall cmdlets are unavailable; configure the inbound port manually.' -ForegroundColor Yellow
        return
    }

    $ruleName = "Team Hub - $siteName - TCP $port"
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $port -Profile Domain,Private | Out-Null
    }
}

function Test-TcpListener([int] $port) {
    $client = [Net.Sockets.TcpClient]::new()
    try {
        $asyncResult = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $asyncResult.AsyncWaitHandle.WaitOne(10000)) {
            return $false
        }
        $client.EndConnect($asyncResult)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

$exitCode = 0
$packageInfo = $null
$backupPath = $null

try {
    Write-Host ''
    Write-Host 'Team Hub Interactive Server Hosting' -ForegroundColor Green
    Write-Host 'This installer preserves existing data and configuration and creates a backup before updating.'
    Write-Host "Detailed log: $logPath"

    $repositoryDefault = Resolve-FullPath (Join-Path $PSScriptRoot '..\..')
    $sourceInput = Read-Default 'Team Hub source repository or pre-published folder' $repositoryDefault
    $siteName = Read-Default 'IIS website name' 'TeamHub'
    $appPoolName = Read-Default 'IIS application-pool name' 'TeamHub'
    $installPath = Assert-SafeInstallPath (Read-Default 'Server installation directory' 'C:\inetpub\TeamHub')
    $hostName = (Read-Required 'Team Hub DNS host name, for example teamhub.company.com').ToLowerInvariant()

    if ($siteName -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$') {
        throw 'The IIS website name contains unsupported characters.'
    }
    if ($appPoolName -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$') {
        throw 'The application-pool name contains unsupported characters.'
    }
    if ($hostName -notmatch '^[a-z0-9][a-z0-9.-]+$') {
        throw 'Enter a valid DNS host name without a protocol, port, or path.'
    }

    $useHttps = Read-YesNo 'Configure HTTPS using a certificate already installed on this server?' $true
    $httpPortPrompt = if ($useHttps) { 'Temporary HTTP port used while configuring IIS' } else { 'HTTP port' }
    $httpPortDefault = if ($useHttps) { 8080 } else { 80 }
    $httpPort = Read-Port $httpPortPrompt $httpPortDefault
    $httpsPort = 443
    $certificateThumbprint = ''
    if ($useHttps) {
        $httpsPort = Read-Port 'HTTPS port' 443
        Write-Step 'Available LocalMachine certificates'
        $certificates = @(Get-ChildItem Cert:\LocalMachine\My |
            Where-Object { $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) } |
            Sort-Object NotAfter -Descending)
        if ($certificates.Count -eq 0) {
            throw 'No valid LocalMachine certificate with a private key is installed. Ask the infrastructure team to install the company certificate first.'
        }
        foreach ($certificate in $certificates) {
            Write-Host ("{0} | expires {1:yyyy-MM-dd} | {2}" -f $certificate.Thumbprint, $certificate.NotAfter, $certificate.Subject)
        }
        $certificateThumbprint = (Read-Required 'Certificate thumbprint').Replace(' ', '').ToUpperInvariant()
    }
    elseif (-not (Read-YesNo 'Continue with unencrypted HTTP? Use this only on a protected test network.' $false)) {
        throw 'Hosting was cancelled because HTTPS was not configured.'
    }

    $configureGraph = Read-YesNo 'Configure Microsoft Graph credentials for SharePoint/OneDrive Excel access?' $false
    $graphTenantId = ''
    $graphClientId = ''
    $graphClientSecret = $null
    if ($configureGraph) {
        $graphTenantId = Read-Required 'Microsoft Entra tenant ID'
        $graphClientId = Read-Required 'Microsoft Entra application client ID'
        $graphClientSecret = Read-Host 'Microsoft Entra application client secret' -AsSecureString
    }

    Ensure-IisInstalled
    Ensure-HostingBundleInstalled
    $packageInfo = Get-TeamHubPackage $sourceInput

    if (Test-Path "IIS:\Sites\$siteName") {
        Stop-Website -Name $siteName -ErrorAction SilentlyContinue
    }
    if (Test-Path "IIS:\AppPools\$appPoolName") {
        Stop-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    $backupPath = Backup-TeamHubDeployment $installPath
    Copy-TeamHubApplication $packageInfo.PackagePath $installPath

    $destinationConfig = Join-Path $installPath 'config'
    $defaultConfigSource = Join-Path $packageInfo.SourceRoot 'config'
    if (-not (Test-Path -LiteralPath $defaultConfigSource -PathType Container)) {
        $defaultConfigSource = Read-Required 'Path to the Team Hub config directory'
    }
    $defaultConfigSource = Resolve-FullPath $defaultConfigSource
    $copyConfig = -not (Test-DirectoryHasContent $destinationConfig)
    if (-not $copyConfig) {
        $copyConfig = Read-YesNo 'Existing production config was preserved. Merge updated config files from the selected source?' $false
    }
    if ($copyConfig) {
        Write-Step 'Deploying Team Hub configuration'
        Copy-DirectoryContents $defaultConfigSource $destinationConfig
    }

    $destinationData = Join-Path $installPath 'data'
    if (-not (Test-DirectoryHasContent $destinationData)) {
        $existingDataSource = (Read-Host 'Existing Team Hub data directory to migrate, or press Enter for a fresh installation').Trim()
        if (-not [string]::IsNullOrWhiteSpace($existingDataSource)) {
            Write-Step 'Migrating the existing Team Hub data directory'
            Copy-DirectoryContents (Resolve-FullPath $existingDataSource) $destinationData
        }
    }

    New-Item -ItemType Directory -Path $destinationData -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destinationData 'team-excel') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $destinationData 'protection-keys') -Force | Out-Null

    Configure-TeamHubIisSite $siteName $appPoolName $installPath $hostName $httpPort $useHttps $httpsPort $certificateThumbprint
    Stop-Website -Name $siteName -ErrorAction SilentlyContinue
    Stop-WebAppPool -Name $appPoolName -ErrorAction SilentlyContinue
    Grant-TeamHubPermissions $installPath $appPoolName

    if ($configureGraph) {
        Write-Step 'Saving Microsoft Graph settings for the Team Hub application pool'
        $plainGraphSecret = Convert-SecureStringToPlainText $graphClientSecret
        try {
            Set-AppPoolEnvironmentVariable $appPoolName 'TeamExcel__MicrosoftGraph__TenantId' $graphTenantId
            Set-AppPoolEnvironmentVariable $appPoolName 'TeamExcel__MicrosoftGraph__ClientId' $graphClientId
            Set-AppPoolEnvironmentVariable $appPoolName 'TeamExcel__MicrosoftGraph__ClientSecret' $plainGraphSecret
        }
        finally {
            $plainGraphSecret = $null
        }
    }

    $accessDatabase = Join-Path $destinationData 'access.db'
    $initializeAdministrator = -not (Test-Path -LiteralPath $accessDatabase -PathType Leaf)
    if (-not $initializeAdministrator) {
        $initializeAdministrator = Read-YesNo 'An access database already exists. Attempt first-admin bootstrap only if this database has no administrator?' $false
    }
    if ($initializeAdministrator) {
        Write-Step 'First administrator details'
        $adminUser = Read-Required 'Administrator email or user ID'
        $adminName = Read-Default 'Administrator display name' $adminUser
        while ($true) {
            $adminPassword = Read-Host 'Initial administrator password (minimum 8 characters)' -AsSecureString
            $confirmation = Read-Host 'Confirm administrator password' -AsSecureString
            $firstValue = Convert-SecureStringToPlainText $adminPassword
            $secondValue = Convert-SecureStringToPlainText $confirmation
            $passwordMatches = $firstValue -ceq $secondValue
            $passwordLongEnough = $firstValue.Length -ge 8
            $firstValue = $null
            $secondValue = $null
            if ($passwordMatches -and $passwordLongEnough) {
                break
            }
            Write-Host 'Passwords must match and contain at least 8 characters.' -ForegroundColor Yellow
        }
        Initialize-TeamHubAdministrator $installPath $adminUser $adminName $adminPassword
    }
    else {
        Write-Host 'Bootstrap administrator creation was skipped.' -ForegroundColor Green
    }

    $publicPort = if ($useHttps) { $httpsPort } else { $httpPort }
    Configure-TeamHubFirewall $siteName $publicPort
    Start-Service -Name W3SVC
    Start-WebAppPool -Name $appPoolName
    Start-Website -Name $siteName
    Start-Sleep -Seconds 3

    $url = if ($useHttps) {
        if ($httpsPort -eq 443) { "https://$hostName" } else { 'https://{0}:{1}' -f $hostName, $httpsPort }
    }
    else {
        'http://{0}:{1}' -f $hostName, $httpPort
    }

    Write-Step 'Hosting completed'
    Write-Host "Team Hub URL: $url" -ForegroundColor Green
    Write-Host "IIS website: $siteName"
    Write-Host "Application pool: $appPoolName"
    Write-Host "Application directory: $installPath"
    Write-Host "Persistent data: $destinationData"
    if ($null -ne $backupPath) {
        Write-Host "Pre-deployment backup: $backupPath"
    }
    Write-Host "Create or verify this DNS record: $hostName -> this server's IP address" -ForegroundColor Yellow
    if (-not (Test-TcpListener $publicPort)) {
        Write-Host "Port $publicPort did not respond locally. Check IIS Event Viewer and the installer log." -ForegroundColor Yellow
    }
    Write-Host "Installer log: $logPath"
}
catch {
    $exitCode = 1
    Write-Host ''
    Write-Host 'TEAM HUB HOSTING FAILED' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Review the detailed log: $logPath" -ForegroundColor Yellow
}
finally {
    if ($null -ne $packageInfo -and -not [string]::IsNullOrWhiteSpace($packageInfo.TemporaryPath)) {
        $temporaryPath = Resolve-FullPath $packageInfo.TemporaryPath
        $tempRoot = (Resolve-FullPath $env:TEMP).TrimEnd('\') + '\'
        if ($temporaryPath.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            (Split-Path -Leaf $temporaryPath).StartsWith('TeamHub-Publish-', [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $temporaryPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Stop-Transcript -ErrorAction SilentlyContinue | Out-Null
}

exit $exitCode
