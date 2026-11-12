#requires -RunAsAdministrator

$ErrorActionPreference = "Stop"
$serviceName = "DocumentTagger"
$installDirectory = Join-Path $env:LOCALAPPDATA "DocumentTagger\Service"
$projectPath = Join-Path $PSScriptRoot "Document Tagger.csproj"
$sourceDatabase = Join-Path $PSScriptRoot "App_Data\documents.db"
$settingsPath = Join-Path $PSScriptRoot "appsettings.json"

$settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
$pdfToPpmPath = $settings.DocumentTagger.PdfToPpmPath
if (-not (Test-Path -LiteralPath $pdfToPpmPath -PathType Leaf)) {
    throw "pdftoppm was not found at '$pdfToPpmPath'. Set DocumentTagger.PdfToPpmPath in appsettings.json to the full path of pdftoppm.exe."
}cd 

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$wasRunning = $existingService -and $existingService.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Running
if ($wasRunning) {
    Stop-Service -Name $serviceName
    $existingService.WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Stopped,
        [TimeSpan]::FromSeconds(30))
}

try {
    & dotnet publish $projectPath --configuration Release --output $installDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $dataDirectory = Join-Path $installDirectory "App_Data"
    New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
    $installedDatabase = Join-Path $dataDirectory "documents.db"
    if ((Test-Path $sourceDatabase) -and -not (Test-Path $installedDatabase)) {
        foreach ($suffix in @("", "-wal", "-shm")) {
            $source = "$sourceDatabase$suffix"
            if (Test-Path $source) {
                Copy-Item -Path $source -Destination "$installedDatabase$suffix"
            }
        }
    }

    if (-not $existingService) {
        $account = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $credential = Get-Credential -UserName $account -Message "Enter the Windows password for the Document Tagger service."
        $executable = Join-Path $installDirectory "Document Tagger.exe"
        New-Service -Name $serviceName `
            -DisplayName "Document Tagger" `
            -Description "Private document library web application" `
            -BinaryPathName "`"$executable`"" `
            -Credential $credential `
            -StartupType Automatic | Out-Null
    }

    Set-Service -Name $serviceName -StartupType Automatic
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))

    $deadline = (Get-Date).AddSeconds(30)
    do {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:5248/sign-in" -TimeoutSec 3 -UseBasicParsing
            if ($response.StatusCode -eq 200) {
                Write-Host "Document Tagger is running at http://localhost:5248."
                exit 0
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    } while ((Get-Date) -lt $deadline)

    throw "The service started but did not respond at http://localhost:5248/sign-in. Check the DocumentTagger service logs."
}
catch {
    if ($wasRunning -and (Get-Service -Name $serviceName).Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
        Start-Service -Name $serviceName
    }
    throw
}
