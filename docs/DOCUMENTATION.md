# ATM Log Agent — Documentation Technique Complète

> **Version** : 2.0 — C# / .NET 8 | Windows Embedded & Linux systemd  
> **Dernière mise à jour** : Mai 2025

---

## Vue d'ensemble

Agent logiciel embarqué sur les ATM pour la **collecte sécurisée**, la **transmission en temps réel** et la **supervision centralisée** des journaux ATM. L'agent s'auto-configure entièrement au démarrage : aucune saisie humaine n'est requise pour l'identité de l'ATM (BankName, Country, City, AtmId).

---

## Architecture

```
AtmLogAgent/
├── src/
│   ├── AtmLogAgent.Core/                  # Logique métier (framework-agnostic)
│   │   ├── Models/
│   │   │   ├── AgentConfiguration.cs      # Modèles de configuration
│   │   │   └── LogModels.cs               # Entités (LogEntry, FileSyncRecord, AuditEvent…)
│   │   ├── Interfaces/
│   │   │   └── IServices.cs               # Contrats + IAtmIdentityResolver
│   │   ├── Parsers/
│   │   │   └── AtmJrnParser.cs            # Parseur format journal ATM propriétaire
│   │   └── Services/
│   │       ├── AtmIdentityResolverService.cs  # ★ Auto-détection identité ATM
│   │       ├── EncryptionService.cs           # Chiffrement AES-256-GCM
│   │       ├── LogDiscoveryService.cs         # Découverte auto des répertoires de logs
│   │       ├── LogWatcherService.cs           # Surveillance temps réel (Channel async)
│   │       ├── LocalBufferService.cs          # Tampon SQLite persistant (zéro perte)
│   │       ├── SftpTransmissionService.cs     # Transmission SFTP + retry Polly
│   │       ├── HealthMonitorService.cs        # Heartbeats + audit
│   │       └── UpdateService.cs              # Mises à jour automatiques + rollback
│   ├── AtmLogAgent.Service/               # Hôte du service Windows/Linux
│   │   ├── Workers/
│   │   │   ├── LogCollectorWorker.cs      # Collecte temps réel
│   │   │   └── Workers.cs                # Transmission, Sync, Update, Health workers
│   │   ├── Program.cs                     # Point d'entrée + résolution identité + DI
│   │   └── appsettings.json              # Configuration (seul SFTP requis)
│   └── AtmLogAgent.Installer/
│       ├── Install-AtmLogAgent.ps1        # Installation Windows
│       ├── install.sh                     # Installation Linux
│       └── provisioning.conf.template     # ★ Fichier de provisionnement bancaire
└── tests/
    └── AtmLogAgent.Tests/
        ├── AgentTests.cs                  # Tests unitaires (Encryption, Buffer, Discovery)
        ├── AtmJrnParserTests.cs           # Tests du parseur .jrn
        ├── JrnFileIntegrationTests.cs     # Tests d'intégration sur vrais fichiers .jrn
        └── TestData/                      # Fichiers journaux ATM réels (BGFI Gabon)
            ├── 20200810.jrn
            ├── 20230418.jrn
            └── 20240512.jrn
```

---

## Flux de données

```
Fichiers .jrn / .log sur ATM
        │
        ▼
LogWatcherService (FileSystemWatcher)
  • Publie les chemins dans un Channel<string> (non-bloquant)
  • Boucle async consommatrice — zéro deadlock
  • Reprend après crash (positions chiffrées sur disque)
        │
        ▼
LogCollectorWorker
  • Détecte le format (JRN, XML, JSON…)
  • Construit le chemin distant normalisé
  • Calcule le checksum SHA-256
        │
        ▼
LocalBufferService (SQLite WAL)
  • Chiffre le contenu (AES-256-GCM)
  • Stocke en base locale (survit aux coupures réseau)
        │
        ▼
TransmissionWorker → SftpTransmissionService
  • Dépile le tampon en continu (lots de 50)
  • Transmet via SFTP (SSH.NET + Polly retry)
  • Vérifie l'intégrité SHA-256 côté serveur
  • Marque complété ou remet en file
        │  SSH/SFTP — RSA 4096
        ▼
Serveur SFTP distant
  Structure : BGFI/GABON/LIBREVILLE/ATM-SN8472KX/YYYY/MM/DD/HHMMSS/fichier.jrn
```

---

## ★ Auto-détection de l'identité ATM

L'agent résout automatiquement son identité complète au démarrage, **sans intervention humaine**. La seule configuration requise par un technicien est l'adresse du serveur SFTP.

### Stratégie de résolution (ordre de priorité)

| Champ | Source 1 (priorité) | Source 2 (fallback) | Source 3 (fallback) |
|-------|--------------------|--------------------|---------------------|
| **AtmId** | Numéro de série BIOS/DMI (`/sys/class/dmi/id/product_serial`) | Adresse MAC interface principale | Nom d'hôte machine |
| **Country** | Géolocalisation IP (ip-api.com) | Fuseau horaire système | Valeur dans config |
| **City** | Géolocalisation IP (ip-api.com) | Fuseau horaire système | Valeur dans config |
| **BankName** | Fichier `provisioning.conf` | Hostname SFTP (`sftp.bgfi.com` → `BGFI`) | Valeur dans config |

### Timeout et résilience

La résolution s'effectue avec un **timeout de 15 secondes**. Si la géolocalisation est indisponible au démarrage (réseau non encore établi), l'agent démarre avec les valeurs partielles (AtmId hardware toujours disponible) et réessaie au prochain cycle.

### Fichier de provisionnement bancaire

Déposé **une seule fois** par le technicien de la banque lors de l'installation initiale :

| OS | Emplacement |
|----|-------------|
| Linux | `/etc/atm-agent/provisioning.conf` |
| Windows | `C:\ProgramData\AtmLogAgent\provisioning.conf` |

**Contenu minimal :**
```ini
# Fichier de provisionnement ATM — AtmLogAgent
# Seul BankName est obligatoire. Tous les autres champs sont auto-détectés.

BankName=BGFI
```

Un template complet est fourni dans `src/AtmLogAgent.Installer/provisioning.conf.template`.

### Traçabilité des sources

Chaque valeur résolue est journalisée avec sa source :
```
[INFO] ATM identity pre-resolved — Bank=BGFI (provisioning_file) |
       GABON/LIBREVILLE (ip_geolocation) | AtmId=ATM-SN8472KX (hardware_serial)
```

---

## Structure des chemins distants

```
{BANQUE}/{PAYS}/{VILLE}/{ATM_ID}/{YYYY}/{MM}/{DD}/{HHMMSS}/{fichier}.jrn
```

**Exemple avec les fichiers journaux BGFI Gabon :**
```
BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2020/08/10/060000/20200810.jrn
BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2023/04/18/115900/20230418.jrn
BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2024/05/12/063000/20240512.jrn
```

> L'`ATM_ID` est maintenant généré automatiquement depuis le numéro de série hardware (ex : `ATM-SN8472KX`) et non plus saisi manuellement.

---

## Sécurité

| Mécanisme | Implémentation |
|-----------|---------------|
| Chiffrement au repos | AES-256-GCM (AEAD — authentifié + intégre) |
| Protection de la clé | DPAPI (Windows) / chmod 600 (Linux) |
| Transport | SFTP + SSH.NET / TLS |
| Authentification SFTP | Clé privée RSA 4096 bits (préféré) ou mot de passe |
| Vérification intégrité | SHA-256 calculé localement + vérifié sur serveur distant |
| Pinning certificat | Fingerprint SSH configurable |
| Mises à jour | Signature RSA + vérification SHA-256 |
| Audit | Journalisation immuable (17 types d'événements) |
| Principe moindre privilège | Utilisateur système dédié, permissions minimales |
| Sandboxing Linux | systemd : `NoNewPrivileges`, `PrivateTmp`, `ProtectSystem=strict` |
| Masquage PAN | `531234******5678` — conformité PCI-DSS systématique |

---

## Parseur ATM JRN

Le parseur `AtmJrnParser` extrait les données structurées des journaux propriétaires (11 regex compilées, 14 types de lignes) :

| Données extraites | Exemple de ligne |
|------------------|--------------------|
| Timestamp | `06:15:00 -> TRANSACTION START` |
| Événement système | `*1000*06:00:02 OPERATOR DOOR OPENED` |
| Code réponse | `CODE REPONSE: 00` |
| Montant | `AMOUNT 30000 ENTERED` / `MONTANT: 50000 XAF` |
| PAN masqué | `TRACK 2 DATA: 531234******5678` |
| Statut périphérique | `DEVICE CCCdmFW STATUS 0 SUPPLY 1` |
| Événement cassette | `*527*11:43:40 THIRD CASSETTE REMOVED` |
| Compteur billets | `CFA 10000 1553*` (★ = estimation avant SOP) |
| RRN | `RRN: 310865672052` |
| AID EMV | `EMV AID A0000000031010` |

**Codes réponse supportés (ISO 8583) :**

| Code | Signification |
|------|--------------|
| `00` | Transaction approuvée |
| `51` | Fonds insuffisants |
| `54` | Carte expirée |
| `75` | PIN incorrect — 3 essais dépassés |

**Détection de fraude :**  
`IsSuspicious = CardRetained && IsApproved` — carte retenue après transaction approuvée.

---

## Formats de logs supportés

| Extension | Format | Détection |
|-----------|--------|-----------| 
| `.jrn` | Propriétaire ATM (NCR APTRA) | Par extension |
| `.log` / `.txt` | Texte brut ou propriétaire | Heuristique sur contenu |
| `.xml` | XML | Par extension |
| `.json` | JSON | Par extension |
| `.csv` | CSV | Par extension |

---

## Résilience réseau

| Scénario | Mécanisme | Comportement |
|----------|-----------|-------------|
| Coupure réseau | SQLite buffer + retry exponentiel | Données locales, transmises à reconnexion |
| Crash de l'agent | Positions chiffrées + SQLite WAL | Reprise exacte à la dernière ligne lue |
| Fichier verrouillé (ATM) | `OpenFileWithRetryAsync` (5 tentatives) | Retry progressif (200ms × n) |
| SFTP indisponible | `Polly.WaitAndRetryAsync` | Backoff : 30s, 60s, 120s, 240s + jitter |
| Buffer saturé (>500 MB) | `HealthWorker` | Alerte au serveur de supervision |
| MAJ défectueuse | `RollbackAsync()` | Retour à la version précédente (max 3) |

---

## Workers Background

| Worker | Rôle | Cycle |
|--------|------|-------|
| `LogCollectorWorker` | Surveillance temps réel + enfilement | Event-driven (Channel async) |
| `TransmissionWorker` | Vidage du tampon vers SFTP (lots de 50) | Continu (500ms pause) |
| `SyncWorker` | Synchronisation complète des fichiers | Toutes les 24h |
| `UpdateWorker` | Vérification et installation mises à jour | Toutes les 6h |
| `HealthWorker` | Heartbeats + audit événements | Toutes les 60s |

---

## Installation

### Ce qui est requis par un humain

Avant toute installation, le technicien de la banque prépare **deux éléments** :

1. **Le fichier de provisionnement** (`provisioning.conf`) contenant `BankName=BGFI`
2. **La configuration SFTP** : adresse du serveur, nom d'utilisateur, clé privée RSA

Tout le reste (Country, City, AtmId) est détecté automatiquement.

---

### Windows

```powershell
# En tant qu'Administrateur — seuls Host et User sont requis
.\Install-AtmLogAgent.ps1 `
    -SftpHost sftp.banque.example.com `
    -SftpUser atm-agent

# Le script dépose automatiquement provisioning.conf
# BankName, Country, City, AtmId sont auto-détectés au premier démarrage
```

### Linux

```bash
# Déposer le fichier de provisionnement
sudo mkdir -p /etc/atm-agent
echo "BankName=BGFI" | sudo tee /etc/atm-agent/provisioning.conf

# Installer le service (seul SFTP requis)
sudo ATMAGENT_SFTP_HOST=sftp.banque.example.com \
     ATMAGENT_SFTP_USER=atm-agent \
     bash install.sh
```

---

## Commandes de gestion

### Windows
```powershell
Start-Service AtmLogAgent          # Démarrer
Stop-Service AtmLogAgent           # Arrêter
Restart-Service AtmLogAgent        # Redémarrer
Get-Service AtmLogAgent            # Vérifier l'état
Get-EventLog -Source AtmLogAgent   # Voir les événements
```

### Linux
```bash
systemctl status atm-log-agent     # État
systemctl restart atm-log-agent    # Redémarrer
journalctl -u atm-log-agent -f     # Logs en temps réel
journalctl -u atm-log-agent --since "1 hour ago"
```

---

## Configuration (`appsettings.json`)

Seule la section `Transmission` nécessite une saisie humaine. La section `Atm` est entièrement auto-détectée (`"AUTO"`).

```json
{
  "AtmAgent": {
    "Atm": {
      "BankName":     "AUTO",
      "Country":      "AUTO",
      "City":         "AUTO",
      "AtmId":        "AUTO",
      "Manufacturer": "NCR",
      "Model":        "SelfServ 84"
    },
    "Transmission": {
      "Protocol":              "SFTP",
      "Host":                  "sftp.banque.example.com",
      "Port":                  22,
      "Username":              "atm-agent",
      "PrivateKeyPath":        "C:\\ProgramData\\AtmLogAgent\\keys\\agent_rsa",
      "CompressBeforeTransmit": true,
      "FullSyncIntervalHours": 24,
      "MaxRetryAttempts":      10
    },
    "Security": {
      "LocalEncryptionKeyId":  "C:\\ProgramData\\AtmLogAgent\\agent.key",
      "EnableIntegrityChecks": true,
      "EnableAuditLog":        true,
      "ServerCertificatePinning": null
    }
  }
}
```

> **Forcer une valeur** : remplacer `"AUTO"` par la valeur souhaitée. Une valeur explicite est toujours prioritaire sur la détection automatique.

---

## Fabricants ATM supportés

| Fabricant | Chemins de logs auto-détectés |
|-----------|-------------------------------|
| NCR | `C:\Program Files\NCR\APTRA\Logs`, `/opt/ncr/logs` |
| Diebold Nixdorf | `C:\Diebold\Logs`, `C:\OPTEEVA\Logs` |
| Wincor / Nixdorf | `C:\Wincor\Logs`, `C:\Program Files\Diebold Nixdorf\ProTopas\Log` |
| Nautilus Hyosung | `C:\Hyosung\Log` |
| GRG Banking | `C:\GRG\Log` |
| Générique | `C:\ATM\Logs`, `/var/log/atm`, `/opt/atm/logs` |

---

## Pré-requis

- **.NET 8 Runtime** (ou supérieur)
- **Windows** : Windows 7 Embedded SP1+ / Windows 10 / Windows Server 2012+
- **Linux** : Ubuntu 18.04+, Debian 9+, CentOS 7+, RHEL 7+
- **Réseau** : Accès SFTP/SSH (port 22) vers le serveur distant + accès temporaire à `ip-api.com` au démarrage (géolocalisation)
- **Disque** : 500 MB minimum pour le tampon local

---

## Tests

```bash
export DOTNET_ROOT=~/.dotnet && export PATH=$PATH:~/.dotnet
cd AtmLogAgent
dotnet test tests/AtmLogAgent.Tests/ -v normal
```

**Couverture des tests :**
- Chiffrement AES-256-GCM (round-trip, détection tamper, checksum, zero-memory)
- Normalisation des chemins (`AtmIdentity.GetBasePath`, `WithResolution`)
- Détection de format de fichier
- Tampon SQLite (enqueue/dequeue, persistance, purge)
- Parseur JRN (timestamps, codes réponse, montants, PAN, cassettes, compteurs)
- **Tests d'intégration sur vrais fichiers .jrn** : `20200810.jrn`, `20230418.jrn`, `20240512.jrn` (BGFI Gabon)

---

## Correctifs appliqués (v2.0)

| Bug | Impact | Correctif |
|-----|--------|-----------|
| `GetAwaiter().GetResult()` dans `LogWatcherService` | Deadlock potentiel en production | Pattern `Channel<string>` producer/consumer async |
| Compression temps réel incorrecte (`Base64(GZip)` dans `.gz`) | Fichiers GZip corrompus, illisibles | Transmission UTF-8 brut ; compression réservée à la sync complète |

---

## Conformité et standards

| Standard | Implémentation |
|----------|---------------|
| **PCI-DSS** | PAN masqué (`531234******5678`), chiffrement au repos, audit trail |
| **ISO 8583** | Codes réponse : 00, 51, 54, 75… |
| **NIST SP 800-38D** | AES-256-GCM, nonce 96 bits, tag 128 bits |
| **NIST SP 800-57** | Gestion des clés, rotation via `UpdateConfig` |
| **Principe moindre privilège** | Compte service dédié sans droits admin |
| **Chiffrement authentifié** | AES-256-GCM (AEAD) — confidentialité + intégrité |
