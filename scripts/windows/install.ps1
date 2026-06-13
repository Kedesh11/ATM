<#
.SYNOPSIS
    Script d'installation automatisée de l'AtmLogAgent sur un terminal ATM Windows.
    
.DESCRIPTION
    Ce script effectue les actions suivantes :
    1. Vérifie les droits Administrateur.
    2. Stoppe et désinstalle le service AtmLogAgent s'il existe déjà.
    3. Copie l'exécutable (depuis le dossier courant) vers C:\Program Files\AtmLogAgent.
    4. Génère une clé SSH ED25519 unique pour l'ATM via ssh-keygen (intégré à Windows 10/Server 2019).
    5. Crée le répertoire de configuration et génère la configuration par défaut.
    6. Inscrit le service dans Windows et le démarre.
    Note : L'identité de l'ATM (Pays, Ville, Banque, MAC) sera résolue de manière 100% autonome par l'Agent au démarrage.
#>

param(
    [string]$SftpHost = $env:ATM_SFTP_HOST,
    [int]$SftpPort = $(if ($env:ATM_SFTP_PORT) { [int]$env:ATM_SFTP_PORT } else { 22 }),
    [string]$SftpUser = $(if ($env:ATM_SFTP_USER) { $env:ATM_SFTP_USER } else { "atm-agent" }),
    [string]$SftpHostKeyFingerprint = $env:ATM_SFTP_HOSTKEY,
    [string]$HeartbeatUrl = $(if ($env:ATM_HEARTBEAT_URL) { $env:ATM_HEARTBEAT_URL } else { "https://supervision.example.com/api/heartbeat" })
)

$ErrorActionPreference = "Stop"

# 1. Vérification des droits Administrateur
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Ce script doit être exécuté en tant qu'Administrateur."
    exit 1
}

$ServiceName = "AtmLogAgent"
$InstallDir = "$env:ProgramFiles\AtmLogAgent"
$DataDir = "$env:ProgramData\AtmLogAgent"
$ConfigDir = "$DataDir\config"
$LogDir = "$DataDir\Logs"
$KeyDir = "$DataDir\keys"
$SshKeyPath = "$KeyDir\agent_ed25519"
$EncryptionKeyPath = "$DataDir\agent.key"

if ([string]::IsNullOrWhiteSpace($SftpHost)) {
    throw "SftpHost est obligatoire. Passez -SftpHost ou définissez ATM_SFTP_HOST."
}

if ([string]::IsNullOrWhiteSpace($SftpHostKeyFingerprint)) {
    throw "SftpHostKeyFingerprint est obligatoire en production. Passez -SftpHostKeyFingerprint ou définissez ATM_SFTP_HOSTKEY."
}

# 2. Gestion du Service Existant
if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host "Arrêt du service existant..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force
    
    # Pause courte pour libérer les fichiers
    Start-Sleep -Seconds 2
    
    Write-Host "Désinstallation de l'ancien service..." -ForegroundColor Yellow
    sc.exe delete $ServiceName
    Start-Sleep -Seconds 1
}

# 3. Création de l'arborescence
Write-Host "Création des répertoires d'installation..." -ForegroundColor Cyan
if (!(Test-Path $InstallDir)) { New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null }
if (!(Test-Path $ConfigDir)) { New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null }
if (!(Test-Path $LogDir)) { New-Item -ItemType Directory -Force -Path $LogDir | Out-Null }
if (!(Test-Path $KeyDir)) { New-Item -ItemType Directory -Force -Path $KeyDir | Out-Null }

# Copie de l'exécutable
$SourceExe = Join-Path $PSScriptRoot "AtmLogAgent.Service.exe"
if (Test-Path $SourceExe) {
    Write-Host "Copie de l'exécutable..." -ForegroundColor Cyan
    Copy-Item -Path $SourceExe -Destination $InstallDir -Force
} else {
    Write-Warning "Le binaire AtmLogAgent.Service.exe est introuvable dans le dossier actuel ($PSScriptRoot)."
    Write-Warning "Assurez-vous de placer l'exécutable compilé avec ce script."
    exit 1
}

# 4. Génération de la Clé Cryptographique / SSH
if (!(Test-Path $SshKeyPath)) {
    Write-Host "Génération de la clé cryptographique SSH..." -ForegroundColor Cyan
    # Utilisation de ssh-keygen (Natif Windows 10+)
    $sshKeygenArgs = "-t ed25519 -f `"$SshKeyPath`" -N `"`" -q"
    Start-Process -FilePath "ssh-keygen.exe" -ArgumentList $sshKeygenArgs -Wait -NoNewWindow
    Write-Host "Clé publique générée : $SshKeyPath.pub" -ForegroundColor Green
    Write-Host "PENSEZ A AJOUTER CETTE CLE AU SERVEUR SFTP !" -ForegroundColor Yellow
} else {
    Write-Host "Clé cryptographique déjà présente." -ForegroundColor Green
}

# 5. Fichiers de Configuration
$AppSettingsPath = "$ConfigDir\appsettings.json"
if (!(Test-Path $AppSettingsPath)) {
    $defaultSettings = @"
	{
	  "AtmAgent": {
	    "Atm": {
	      "BankName": "AUTO",
	      "Country": "AUTO",
	      "City": "AUTO",
	      "AtmId": "AUTO",
	      "Manufacturer": "AUTO"
	    },
	    "Transmission": {
	      "Protocol": "SFTP",
	      "Host": "$SftpHost",
	      "Port": $SftpPort,
	      "Username": "$SftpUser",
	      "PrivateKeyPath": "$($SshKeyPath.Replace('\','\\'))",
	      "PrivateKeyPassphrase": null,
	      "RemoteBasePath": "/atm-logs",
	      "CompressBeforeTransmit": true,
	      "MaxConcurrentTransfers": 3,
	      "MaxRetryAttempts": 10,
	      "RetryDelaySeconds": 30,
	      "FullSyncIntervalHours": 24,
	      "ConnectionTimeoutSeconds": 30,
	      "KeepAliveIntervalSeconds": 60
	    },
	    "Security": {
	      "LocalEncryptionKeyId": "$($EncryptionKeyPath.Replace('\','\\'))",
	      "EnableIntegrityChecks": true,
	      "EnableTamperDetection": true,
	      "ValidateServerCertificate": true,
	      "ServerCertificatePinning": "$SftpHostKeyFingerprint",
	      "EnableAuditLog": true,
	      "AuditLogPath": "$($LogDir.Replace('\','\\'))\\audit.log"
	    },
	    "LogDiscovery": {
	      "WatchPaths": [],
	      "FilePatterns": [ "*.jrn", "*.log", "*.txt", "*.xml", "*.json" ],
	      "AutoDiscoverAtmPaths": true,
	      "IncludeSubdirectories": true,
	      "ExcludedPaths": [
	        "C:\\Windows\\System32",
	        "$($DataDir.Replace('\','\\'))"
	      ],
	      "PollingIntervalMs": 500
	    },
	    "Update": {
	      "UpdateServerUrl": "https://updates.atm-agent.example.com/api/v1",
	      "UpdatePublicKeyPath": "$($KeyDir.Replace('\','\\'))\\update_pub.pem",
	      "CheckIntervalHours": 6,
	      "EnableAutoUpdate": false,
	      "AllowHotReload": false,
	      "MaxRollbackVersions": 3
	    },
	    "Monitoring": {
	      "HeartbeatUrl": "$HeartbeatUrl",
	      "HeartbeatIntervalSeconds": 60,
	      "ReportDeviceStatuses": true,
	      "ReportTransactionStats": true,
	      "AlertThresholdBufferSizeMb": 100,
	      "AlertThresholdOfflineMinutes": 30
	    },
	    "Retention": {
	      "LocalLogRetentionDays": 30,
	      "BufferedDataRetentionDays": 7,
	      "MaxLocalBufferSizeMb": 500,
	      "CompressArchivedLogs": true
	    }
	  }
	}
"@
    Set-Content -Path $AppSettingsPath -Value $defaultSettings -Encoding UTF8
    Write-Host "Configuration par défaut créée : $AppSettingsPath" -ForegroundColor Cyan
}

# L'Agent résoudra son identité (MAC, Pays, Ville) de façon 100% autonome au démarrage.

# 6. Enregistrement du Service
Write-Host "Enregistrement du service Windows..." -ForegroundColor Cyan
$ExePath = Join-Path $InstallDir "AtmLogAgent.Service.exe"
# Ajout de l'argument --configdir lors du démarrage du service
$BinPath = "`"$ExePath`" --configdir `"$ConfigDir`""

# Utilisation de sc.exe pour un contrôle plus précis (New-Service gère mal les arguments complexes)
sc.exe create $ServiceName binPath= $BinPath start= auto displayname= "ATM Log Agent (Sawa)"
sc.exe description $ServiceName "Service de collecte et transmission des journaux de bord de l'ATM."

# 7. Démarrage
Write-Host "Démarrage du service..." -ForegroundColor Cyan
Start-Service $ServiceName

Write-Host "=== Installation Terminée avec Succès ===" -ForegroundColor Green
Write-Host "Veuillez vérifier les logs dans $LogDir pour confirmer la bonne exécution."
