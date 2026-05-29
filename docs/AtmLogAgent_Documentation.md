# ATM LOG AGENT
## Documentation Technique & Guide d'Installation

**Agent sécurisé de collecte, supervision et transmission de logs pour environnements ATM bancaires**
- **Version :** 1.0.0 — .NET 8
- **Compatibilité :** Windows 10/11/Server / Linux
- **Classification :** CONFIDENTIEL — Usage interne
- **Protocoles :** SFTP / SSH / TLS

---

## 1. Présentation générale
L'ATM Log Agent est un service logiciel développé en C# / .NET 8, conçu pour fonctionner de manière totalement autonome sur tout type de distributeur automatique de billets (DAB/ATM). Il assure la collecte sécurisée des journaux de transactions, leur transmission en temps réel vers une infrastructure distante centralisée, ainsi que la supervision continue de l'état de santé des ATM.

L'agent opère comme un service système natif démarrant automatiquement au lancement de l'ATM, sans aucune interaction utilisateur requise (Zero-Touch). Il est conçu pour fonctionner 24 heures sur 24 et 7 jours sur 7.

### 1.1 Objectifs principaux
- Collecte sécurisée et en temps réel des logs ATM (fichiers `.jrn`, `.log`).
- Transmission chiffrée vers un serveur distant via SFTP/SSH.
- Résolution 100% autonome de l'identité de l'ATM (Bank, Location, MAC Address).
- Supervision centralisée avec heartbeats périodiques.
- Journalisation d'audit immuable de tous les événements critiques.
- Zéro perte de données grâce à un buffer SQLite WAL crypté.

### 1.2 Fabricants ATM supportés
L'agent détecte automatiquement les répertoires de logs selon le fabricant configuré.
- **NCR** : `C:\Program Files\NCR\APTRA\Logs` (`.jrn`)
- **Diebold Nixdorf** : `C:\Diebold\Logs` (`.jrn`, `.log`)
- **Wincor Nixdorf** : `C:\Wincor\Logs` (`.jrn`, `.log`)

---

## 2. Architecture logicielle
### 2.1 Structure du projet
L'architecture est construite autour du pattern *Worker Service* de .NET 8 :
- **AtmLogAgent.Core** : Cœur métier indépendant du framework (Chiffrement, Découverte, SQLite, SFTP, Résolution d'identité).
- **AtmLogAgent.Service** : L'hôte d'exécution (Service Windows / Systemd Linux) et ses *BackgroundWorkers*.

### 2.2 Flux de données
1. **Découverte** : `LogDiscoveryService` identifie les répertoires valides.
2. **Surveillance** : `LogWatcherService` détecte les modifications en temps réel via `FileSystemWatcher`.
3. **Mise en file d'attente** : `LocalBufferService` stocke l'événement dans une base SQLite chiffrée.
4. **Transmission** : `TransmissionWorker` dépile le SQLite et transmet via le **Pool SFTP** concurrent.

### 2.3 Workers d'arrière-plan
- **LogCollectorWorker** : Surveille les fichiers en temps réel.
- **TransmissionWorker** : Transmet les entrées vers le serveur SFTP (Thread-Safe).
- **HealthWorker** : Envoie un heartbeat régulier (port 443).
- **SyncWorker** : Effectue une resynchronisation complète tous les X heures.

---

## 3. Structure des chemins distants
### 3.1 Format obligatoire
L'arborescence sur le serveur SFTP central est la suivante :
`/upload/{BankName}/{Country}/{City}/{AtmId}/{YYYY}/{MM}/{DD}/{Filename}`

### 3.2 Règles de normalisation
L'agent utilise son service autonome (`AtmIdentityResolverService`) pour :
- Déduire la localisation (Pays/Ville) via IP Geolocation (API HTTPS) ou fuseau horaire.
- Déduire l'identifiant ATM via l'adresse MAC (ex: `ATM-CE7C8111C184`).

---

## 4. Sécurité et conformité bancaire
### 4.1 Mécanismes de sécurité
Le système est durci pour résister aux attaques réseau et physiques. Aucune donnée en clair n'est stockée.

### 4.2 Chiffrement AES-256-GCM
Toutes les données tamponnées localement sont chiffrées en AES-256-GCM. 

### 4.3 Gestion des clés de chiffrement
- **Dérivation SHA-256** : La clé fournie est systématiquement hachée via SHA-256 pour générer la clé cryptographique stricte de 32 octets requise par AES-GCM.
- **Support DPAPI Windows** : Sur Windows, l'agent utilise par défaut DPAPI (`ProtectedData`). S'il détecte une clé asymétrique non-compatible DPAPI, il effectue un *fallback* transparent.

### 4.4 Authentification SFTP par clé privée
L'agent utilise l'authentification forte via paire de clés `ED25519`. Le mot de passe n'est jamais utilisé.

---

## 5. Parseur de journaux ATM (.jrn)
### 5.1 Formats de lignes supportés
L'agent interprète le format `JRN` natif (NCR/Diebold), identifiant les transactions de retrait, de dépôt, et de maintenance.

### 5.2 Codes réponse ISO 8583
Le `AtmJrnParser` extrait et masque les données sensibles (comme les PAN) tout en identifiant les codes réponse d'autorisation réseau (ex: 00 = Approuvé).

### 5.3 Détection d'anomalies
L'agent remonte des métadonnées d'alerte lors de la détection de codes de capture de cartes ou d'erreurs d'encaissement.

---

## 6. Résilience et gestion réseau
### 6.1 Tampon local SQLite
Utilisation d'une base de données SQLite en mode WAL (Write-Ahead Logging) chiffrée. En cas de coupure réseau, les logs s'accumulent localement sans saturation de RAM.

### 6.2 Politique de retry exponentielle
Grâce à la librairie `Polly`, les coupures réseau déclenchent des reconnexions exponentielles (Retry pattern) sans bloquer le thread de collecte.

### 6.3 Reprise de position après crash
L'agent garde en mémoire la position de lecture (offset) exacte de chaque fichier surveillé. Après un redémarrage (ou crash de l'OS), l'agent reprend la lecture au byte près.

---

## 7. Configuration
### 7.1 Fichier appsettings.json
Généré automatiquement lors de l'installation, il contient la configuration du point de montage SFTP et les cibles de logs. Aucune identité d'ATM n'y figure car elle est calculée dynamiquement.

---

## 8. Installation sur Windows
### 8.1 Prérequis
- Windows 10 ou Windows Server 2019 minimum.
- Exécution PowerShell en tant qu'Administrateur.

### 8.2 Procédure d'installation
**Étape 1 — Exécution du script d'installation (Zero-Touch)**
Lancer simplement :
```powershell
.\scripts\windows\install.ps1
```

**Étape 2 — Autorisation de la clé SSH sur le serveur**
Le script génère une clé publique (`C:\ProgramData\AtmLogAgent\agent.key.pub`). Son contenu doit être ajouté au `authorized_keys` du compte `atmagent` sur le serveur SFTP.

**Étape 3 — Vérification du démarrage**
L'agent est inscrit au Service Control Manager (SCM). Vérifier les logs dans `C:\ProgramData\AtmLogAgent\Logs\`.

### 8.3 Actions du script d'installation
Le script s'assure d'arrêter l'ancien service, copie l'exécutable natif `win-x64`, génère les clés ED25519, crée le `appsettings.json`, puis enregistre l'Agent auprès de `sc.exe` en `auto-start`.

### 8.4 Commandes de gestion Windows
- Démarrer : `Start-Service AtmLogAgent`
- Arrêter : `Stop-Service AtmLogAgent`

---

## 9. Installation sur Linux
### 9.1 Prérequis
- OS compatible Systemd (Ubuntu 22.04, Debian).

### 9.2 Procédure d'installation
**Étape 1 — Configuration**
L'exécutable doit être copié dans `/opt/atm-agent/`. Les clés SSH générées par `ssh-keygen -t ed25519` doivent être placées dans `/etc/atm-agent/`.

**Étape 2 — Intégration Systemd**
Création du service (`/etc/systemd/system/atmlogagent.service`), configuration via `systemctl enable atmlogagent`.

### 9.3 Actions du script d'installation Linux
Configuration sécurisée des permissions du fichier `agent.key` (`chmod 600`).

### 9.4 Commandes de gestion Linux
- Démarrer : `systemctl start atmlogagent`
- Arrêter : `systemctl stop atmlogagent`

---

## 10. Mise à jour automatique
### 10.1 Processus de mise à jour
Le `UpdateWorker` se connecte en HTTPS au serveur central une fois par jour pour vérifier l'existence de nouveaux binaires compatibles avec l'architecture (`win-x64` ou `linux-x64`).

### 10.2 Sécurité des mises à jour
Les packages de mises à jour sont signés cryptographiquement.

### 10.3 Politique de rollback
Si l'installation de la mise à jour échoue (ou si le heartbeat échoue après mise à jour), l'agent réinstalle l'ancienne version conservée en backup.

---

## 11. Supervision et monitoring
### 11.1 Heartbeats
Un signal de vie est envoyé au serveur central de supervision (port 443) toutes les X minutes, incluant le `AtmId` et la taille du tampon local.

### 11.2 Journal d'audit
Consignation des événements critiques (Démarrage, Changement de configuration, Perte réseau).

### 11.3 Événements d'audit enregistrés
- `AgentStarted`, `AgentStopped`
- `NetworkLost`, `NetworkRestored`

---

## 12. Diagnostics et dépannage
### 12.1 Vérifications post-installation
Vérifier que les fichiers se déversent bien sur le SFTP dans le dossier cible (GABON/LIBREVILLE/...).

### 12.2 Problèmes courants
- **Échec de connexion SFTP** : Le compte `atmagent` du serveur OpenSSH Linux peut être verrouillé dans `/etc/shadow`. Remplacez le `!` par `*`.
- **Données non transmises** : Vérifiez que l'adresse MAC permet l'authentification et que le pare-feu bancaire autorise le port SFTP.

---

## 13. Conformité et bonnes pratiques
### 13.1 Conformité PCI-DSS
L'agent ne transmet ni ne conserve jamais de numéros de cartes en clair. 
### 13.2 Principe du moindre privilège
Le service Windows tourne avec les droits restreints à ses propres répertoires et ceux des logs.
### 13.3 Standards cryptographiques
Seul l'algorithme `AES-256-GCM` et les échanges `ED25519` sont autorisés.

---

## 14. Exécution des tests
### 14.1 Lancer les tests
Les suites de tests xUnit valident le parsing des logs (`AtmJrnParserTests`), la résilience, et la compatibilité OS croisée.

### 14.2 Couverture des tests
Couvre notamment la validation des événements de maintenance, des codes de retraits, et le respect des expressions régulières (Regex).

---

## 15. Glossaire
- **AES-256-GCM** : Chiffrement authentifié à très haute sécurité.
- **DPAPI** : Data Protection API (Spécifique à Windows).
- **WAL** : Write-Ahead Logging (Sécurisation des écritures base de données).
- **JRN** : Journal d'événements propriétaire des GAB/DAB.
