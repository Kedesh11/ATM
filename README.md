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
- ⚙️ **Service Natif OS** : S'exécute nativement sous forme de Service Windows (SCM) ou de daemon `systemd` sous Linux.
- 📜 **Parsing Avancé** : Décode et masque à la volée les fichiers propriétaires `.jrn` des fabricants (NCR, Diebold Nixdorf).

## 📂 Structure du projet

```bash
AtmLogAgent/
├── src/
│   ├── AtmLogAgent.Core/     # Moteur (Cryptographie, SFTP, Parseurs)
│   └── AtmLogAgent.Service/  # Exécutable natif (Worker Service)
├── scripts/
│   └── windows/
│       └── install.ps1       # Installeur PowerShell "Zero-Touch"
└── docs/                     # Documentations d'architecture
```

## 🚀 Installation & Déploiement

### Déploiement Windows (Recommandé pour les ATMs)

1. **Compilation** de l'agent en exécutable autonome :
   ```bash
   dotnet publish src/AtmLogAgent.Service/AtmLogAgent.Service.csproj -c Release -r win-x64 --self-contained true
   ```
2. **Installation sur l'ATM** : Ouvrez un terminal PowerShell en tant qu'Administrateur et lancez le script (qui copie le binaire, génère la clé SSH, et installe le service Windows) :
   ```powershell
   .\scripts\windows\install.ps1
   ```
3. **Autorisation** : Copiez le contenu de `C:\ProgramData\AtmLogAgent\agent.key.pub` vers le serveur SFTP (fichier `authorized_keys`).

### Déploiement Linux

L'application supporte `systemd`. Référez-vous à la documentation détaillée pour les instructions de configuration Linux.

## 📖 Documentation Détaillée

Pour une plongée en profondeur dans l'architecture, la conformité de sécurité et les mécanismes de retry/résilience, consultez la **[Documentation Officielle](docs/AtmLogAgent_Documentation.md)**.

## 🧪 Tests

Le projet inclut une vaste suite de tests xUnit validant les parseurs, le buffer SQLite et les flux réseau :
```bash
dotnet test tests/AtmLogAgent.Tests/
```

---
*Développé pour les environnements critiques bancaires.*
