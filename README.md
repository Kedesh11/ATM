# 🏦 ATM Log Agent

![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=for-the-badge&logo=dotnet)
![Windows](https://img.shields.io/badge/Windows-0078D6?style=for-the-badge&logo=windows&logoColor=white)
![Linux](https://img.shields.io/badge/Linux-FCC624?style=for-the-badge&logo=linux&logoColor=black)

**AtmLogAgent** est un service natif C#/.NET 8 de très haute disponibilité conçu pour la collecte, la sécurisation, et la transmission temps réel des journaux de bord (logs matériels) des distributeurs automatiques de billets (ATM/DAB).

## 🌟 Fonctionnalités Clés

- 🤖 **100% Autonome (Zero-Touch)** : Résout automatiquement son identité (MAC Address, Localisation géographique via IP/TimeZone). Aucun fichier de configuration manuel requis au déploiement.
- 📡 **Transmission Résiliente** : Transfert par pool de connexions SFTP (`ConcurrentBag`) empêchant les corruptions. Reprise automatique des téléchargements après coupure réseau.
- 🔒 **Haute Sécurité Bancaire (PCI-DSS)** :
  - Buffer local propulsé par **SQLite WAL chiffré en AES-256-GCM**.
  - Clés privées (ED25519) hachées par `SHA-256`.
  - Intégration de `DPAPI` pour la protection des données sur les environnements Windows matériels.
- 📜 **Parsing Avancé** : Décode et masque à la volée les fichiers propriétaires `.jrn` des fabricants (NCR, Diebold Nixdorf).

---

## 🚀 Processus d'Installation de bout-en-bout (Windows ATM)

L'installation sur les terminaux bancaires (NCR, Diebold, Wincor) équipés de Windows est totalement automatisée grâce à notre installeur PowerShell "Zero-Touch".

### Étape 1 : Préparation du Binaire
L'agent doit être compilé en un exécutable autonome (sans dépendance .NET requise sur la machine cible).
Sur votre machine de développement :
```bash
dotnet publish src/AtmLogAgent.Service/AtmLogAgent.Service.csproj -c Release -r win-x64 --self-contained true
```

### Étape 2 : Déploiement sur l'ATM
Transférez le dossier contenant l'exécutable `AtmLogAgent.Service.exe` et le script `install.ps1` sur l'ATM cible.

1. Ouvrez un terminal **PowerShell en tant qu'Administrateur**.
2. Exécutez le script d'installation :
   ```powershell
   .\scripts\windows\install.ps1
   ```

**Que fait ce script ?**
- Il copie l'exécutable dans le dossier sécurisé `C:\Program Files\AtmLogAgent\`.
- Il crée l'environnement de configuration dans `C:\ProgramData\AtmLogAgent\`.
- Il génère silencieusement une clé cryptographique asymétrique de très haute sécurité (`agent.key` via `ssh-keygen.exe` natif).
- Il inscrit l'Agent en tant que **Service Windows** auprès du SCM (Service Control Manager) avec un lancement automatique.
- Il démarre le service immédiatement.

### Étape 3 : Autorisation de l'ATM sur le Serveur Central (SFTP)
Pour que l'ATM puisse transmettre ses logs, le serveur SFTP central doit reconnaître sa clé de sécurité.
1. Récupérez le fichier de clé publique généré par le script sur l'ATM :
   `C:\ProgramData\AtmLogAgent\agent.key.pub`
2. Copiez le contenu de ce fichier et ajoutez-le dans le fichier `~/.ssh/authorized_keys` de l'utilisateur `atmagent` sur votre serveur SFTP.

### Étape 4 : Vérification
Dès l'étape 3 complétée, l'Agent (qui tourne en arrière-plan) détectera son identité, s'authentifiera sur le serveur SFTP, et commencera la transmission des logs instantanément de manière cryptée et sécurisée.

Vous pouvez vérifier le bon fonctionnement en observant les logs de l'Agent sur l'ATM :
`C:\ProgramData\AtmLogAgent\Logs\agent-xxxx.log`

---

## 📖 Documentation d'Architecture

Pour une plongée en profondeur dans l'architecture logicielle, la conformité de sécurité (DPAPI, AES) et les mécanismes de retry/résilience, consultez la **[Documentation Officielle](docs/AtmLogAgent_Documentation.md)**.
