#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Script d'installation de l'ATM Log Agent (Windows)
.DESCRIPTION
    Installe l'agent en tant que service Windows natif (SCM).
    Détection automatique de l'environnement, configuration initiale,
    génération des clés de chiffrement, démarrage automatique.
.PARAMETER ServiceName
    Nom du service Windows (défaut: AtmLogAgent)
.PARAMETER InstallPath
    Répertoire d'installation (défaut: C:\Program Files\AtmLogAgent)
.PARAMETER BankName
    Nom de la banque (ex: BGFI)
.PARAMETER Country
    Pays (ex: GABON)
.PARAMETER City
    Ville (ex: LIBREVILLE)
.PARAMETER AtmId
    Identifiant unique de l'ATM (ex: ATM_001)
.PARAMETER SftpHost
    Adresse du serveur SFTP distant
.PARAMETER SftpPort
    Port SFTP (défaut: 22)
.PARAMETER SftpUser
    Nom d'utilisateur SFTP
.EXAMPLE
    .\Install-AtmLogAgent.ps1 -BankName BGFI -Country GABON -City LIBREVILLE -AtmId ATM_001 -SftpHost sftp.banque.ga -SftpUser atm-agent
#>

param(
    [string]$ServiceName   = "AtmLogAgent",
    [string]$InstallPath   = "C:\Program Files\AtmLogAgent",
    [string]$BankName      = "AUTO",
    [string]$Country       = "AUTO",
    [string]$City          = "AUTO",
    [string]$AtmId         = "AUTO",
    [Parameter(Mandatory)][string]$SftpHost,
    [int]$SftpPort         = 22,
    [Parameter(Mandatory)][string]$SftpUser,
    [Parameter(Mandatory)][string]$SftpHostKeyFingerprint,
    [string]$HeartbeatUrl = "https://supervision.example.com/api/heartbeat"
)

$ErrorActionPreference = "Stop"
$DataPath = "C:\ProgramData\AtmLogAgent"

Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  ATM Log Agent — Installation Windows" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ── Vérifications préalables ────────────────────────────────
Write-Host "[1/8] Vérification de l'environnement..." -ForegroundColor Yellow

$dotnetVersion = & dotnet --version 2>$null
if (-not $dotnetVersion) {
    throw "ERREUR : .NET Runtime non trouvé. Installez .NET 8 Runtime depuis https://dotnet.microsoft.com"
}
Write-Host "      .NET Runtime détecté : $dotnetVersion" -ForegroundColor Green

# Vérifier si le service existe déjà
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "      Service existant détecté — arrêt en cours..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

# ── Création des répertoires ────────────────────────────────
Write-Host "[2/8] Création des répertoires..." -ForegroundColor Yellow
$dirs = @($InstallPath, "$DataPath\Logs", "$DataPath\keys", "$DataPath\Backups")
foreach ($dir in $dirs) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}
Write-Host "      Répertoires créés" -ForegroundColor Green

# ── Copie des fichiers binaires ─────────────────────────────
Write-Host "[3/8] Copie des fichiers de l'agent..." -ForegroundColor Yellow
$sourceDir = $PSScriptRoot
Copy-Item -Path "$sourceDir\*" -Destination $InstallPath -Recurse -Force -Exclude "*.ps1"
Write-Host "      Fichiers copiés vers $InstallPath" -ForegroundColor Green

# ── Configuration ───────────────────────────────────────────
Write-Host "[4/8] Génération de la configuration..." -ForegroundColor Yellow

$configContent = @"
{
  "AtmAgent": {
    "Atm": {
      "BankName": "$BankName",
      "Country": "$Country",
      "City": "$City",
      "AtmId": "$AtmId",
      "Manufacturer": "AUTO"
    },
    "Transmission": {
      "Protocol": "SFTP",
      "Host": "$SftpHost",
      "Port": $SftpPort,
      "Username": "$SftpUser",
      "PrivateKeyPath": "$($DataPath.Replace('\','\\'))\\keys\\agent_ed25519",
      "CompressBeforeTransmit": true,
      "MaxConcurrentTransfers": 3,
      "MaxRetryAttempts": 10,
      "RetryDelaySeconds": 30,
      "FullSyncIntervalHours": 24
    },
    "Security": {
      "LocalEncryptionKeyId": "$($DataPath.Replace('\','\\'))\\agent.key",
      "EnableIntegrityChecks": true,
      "EnableTamperDetection": true,
      "ValidateServerCertificate": true,
      "ServerCertificatePinning": "$SftpHostKeyFingerprint",
      "EnableAuditLog": true,
      "AuditLogPath": "$($DataPath.Replace('\','\\'))\\Logs\\audit.log"
    },
    "LogDiscovery": {
      "WatchPaths": [],
      "FilePatterns": ["*.jrn", "*.log", "*.txt", "*.xml", "*.json"],
      "AutoDiscoverAtmPaths": true,
      "IncludeSubdirectories": true,
      "ExcludedPaths": ["C:\\\\Windows\\\\System32", "$($DataPath.Replace('\','\\\\'))"]
    },
    "Update": {
      "UpdateServerUrl": "https://updates.atm-agent.example.com/api/v1",
      "UpdatePublicKeyPath": "$($DataPath.Replace('\','\\'))\\keys\\update_pub.pem",
      "CheckIntervalHours": 6,
      "EnableAutoUpdate": false,
      "AllowHotReload": false,
      "MaxRollbackVersions": 3
    },
    "Monitoring": {
      "HeartbeatUrl": "$HeartbeatUrl",
      "HeartbeatIntervalSeconds": 60
    },
    "Retention": {
      "LocalLogRetentionDays": 30,
      "BufferedDataRetentionDays": 7,
      "MaxLocalBufferSizeMb": 500
    }
  }
}
"@

$configContent | Set-Content -Path "$InstallPath\appsettings.json" -Encoding UTF8
Write-Host "      Configuration générée" -ForegroundColor Green

# ── Génération de la paire de clés SSH ─────────────────────
Write-Host "[5/8] Génération de la paire de clés SSH..." -ForegroundColor Yellow
$keyPath = "$DataPath\keys\agent_ed25519"
if (-not (Test-Path $keyPath)) {
    & ssh-keygen -t ed25519 -f $keyPath -N "" -C "atm-agent-$AtmId" 2>&1 | Out-Null
    Write-Host "      Clé ED25519 générée : $keyPath" -ForegroundColor Green
    Write-Host ""
    Write-Host "      ╔══════════════════════════════════════════╗" -ForegroundColor Yellow
    Write-Host "      ║  ACTION REQUISE : Copier la clé publique ║" -ForegroundColor Yellow
    Write-Host "      ║  sur le serveur SFTP ($SftpHost)         ║" -ForegroundColor Yellow
    Write-Host "      ╚══════════════════════════════════════════╝" -ForegroundColor Yellow
    Write-Host "      Clé publique :" -ForegroundColor Cyan
    Get-Content "$keyPath.pub" | Write-Host -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "      Clé existante conservée" -ForegroundColor Cyan
}

# ── Permissions restrictives ────────────────────────────────
Write-Host "[6/8] Application des permissions de sécurité..." -ForegroundColor Yellow
$acl = Get-Acl $DataPath
$acl.SetAccessRuleProtection($true, $false)
$adminRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$systemRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($adminRule)
$acl.AddAccessRule($systemRule)
Set-Acl -Path $DataPath -AclObject $acl
Write-Host "      Permissions appliquées (SYSTEM + Administrators uniquement)" -ForegroundColor Green

# ── Création du service Windows ─────────────────────────────
Write-Host "[7/8] Enregistrement du service Windows..." -ForegroundColor Yellow

$servicePath = "$InstallPath\AtmLogAgent.Service.exe"
New-Service `
    -Name $ServiceName `
    -DisplayName "ATM Log Agent - $BankName $AtmId" `
    -Description "Agent de collecte et transmission sécurisée des logs ATM vers l'infrastructure centrale." `
    -BinaryPathName $servicePath `
    -StartupType Automatic `
    -ErrorVariable serviceError 2>&1 | Out-Null

# Configurer le redémarrage automatique en cas de crash
& sc.exe failure $ServiceName reset= 3600 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "      Service '$ServiceName' enregistré (démarrage automatique)" -ForegroundColor Green

# ── Démarrage du service ────────────────────────────────────
Write-Host "[8/8] Démarrage du service..." -ForegroundColor Yellow
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
if ($svc.Status -eq "Running") {
    Write-Host "      Service démarré avec succès !" -ForegroundColor Green
} else {
    Write-Host "      ATTENTION : Le service n'est pas en cours d'exécution. Statut : $($svc.Status)" -ForegroundColor Red
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  Installation terminée !" -ForegroundColor Green
Write-Host "═══════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""
Write-Host "  ATM          : $BankName / $Country / $City / $AtmId" -ForegroundColor White
Write-Host "  Service      : $ServiceName ($($svc.Status))" -ForegroundColor White
Write-Host "  Installation : $InstallPath" -ForegroundColor White
Write-Host "  Données      : $DataPath" -ForegroundColor White
Write-Host "  Logs agent   : $DataPath\Logs\" -ForegroundColor White
Write-Host "  Audit        : $DataPath\Logs\audit.log" -ForegroundColor White
Write-Host ""
Write-Host "  Commandes utiles :" -ForegroundColor Yellow
Write-Host "    Start-Service $ServiceName" -ForegroundColor Gray
Write-Host "    Stop-Service $ServiceName" -ForegroundColor Gray
Write-Host "    Get-Service $ServiceName" -ForegroundColor Gray
Write-Host ""
