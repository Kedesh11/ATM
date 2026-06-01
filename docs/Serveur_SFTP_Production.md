# Spécifications du Serveur SFTP de Production

Ce document détaille les prérequis techniques, l'architecture et la configuration de sécurité requises pour le serveur SFTP central en production. Ce serveur est la destination finale de tous les journaux de transactions envoyés par la flotte d'automates via **AtmLogAgent**.

---

## 1. Exigences d'Infrastructure

### Système d'exploitation recommandé
* **OS** : Linux Enterprise (Ubuntu 22.04 LTS / 24.04 LTS, RHEL 9, ou Debian 12).
* **Serveur SSH/SFTP** : `OpenSSH-Server` (version 8.x ou supérieure).

### Réseau et Flux
* **IP** : Le serveur doit disposer d'une IP fixe au sein du réseau privé virtuel (VPN) bancaire ou de la zone démilitarisée (DMZ) interne.
* **Port** : `22` (TCP) ou un port personnalisé (ex: `2222`) accessible uniquement depuis le sous-réseau abritant les terminaux ATM.

---

## 2. Configuration Applicative et Fonctionnelle

L'**AtmLogAgent** impose plusieurs contraintes strictes au serveur SFTP distant pour assurer un fonctionnement "Zero-Data-Loss" (zéro perte de données) :

### 2.1 Support obligatoire du mode Append (Ajout binaire)
L'agent écrit les lignes de transactions en temps réel dans les fichiers `.jrn`. Pour éviter d'écraser l'historique de la journée ou de transférer l'intégralité du fichier à chaque transaction, le serveur **DOIT absolument supporter les requêtes SFTP avec le flag `SSH_FXF_APPEND`**. 
> *Note : `OpenSSH` supporte nativement ce flag de manière robuste.*

### 2.2 Permissions de Création d'Arborescence
L'agent est totalement autonome et crée dynamiquement l'arborescence de classement lors de l'envoi de fichiers. L'utilisateur SFTP (ex: `atmagent`) doit avoir les droits de création de dossiers (`mkdir`) à la racine de son espace de dépôt (`upload/`).

Arborescence générée par le bot :
```text
/upload/[BankName]/[Country]/[City]/[AtmId]/[YYYY]/[MM]/[DD]/current.jrn
```
*Exemple : `/upload/BGFI/GABON/LIBREVILLE/ATM-001/2026/06/01/current.jrn`*

---

## 3. Configuration de la Sécurité (Hardening)

Il est vital de sécuriser le point d'entrée SFTP pour empêcher toute compromission latérale si un automate venait à être compromis.

### 3.1 Authentification Exclusive par Clé SSH
Les mots de passe doivent être **strictement désactivés** pour l'utilisateur de service de l'agent.
* L'authentification se fait via une paire de clés cryptographiques (idéalement Ed25519 ou RSA 4096 bits).
* L'agent utilise le fichier de clé privée (`atmagent.key` ou `id_rsa`) provisionné lors de son installation.
* La clé publique de tous les agents (ou une clé par agent si PKI) doit être ajoutée au fichier `~/.ssh/authorized_keys` de l'utilisateur SFTP sur le serveur.

### 3.2 Isolation (Chroot Jail)
L'utilisateur de l'agent (`atmagent`) doit être enfermé dans son répertoire de travail (Chroot) et ne **doit pas avoir d'accès shell**.

**Exemple de configuration dans `/etc/ssh/sshd_config` :**
```sshdconfig
# Désactiver le shell par défaut pour l'utilisateur
Match User atmagent
    ForceCommand internal-sftp
    PasswordAuthentication no
    PubkeyAuthentication yes
    PermitTunnel no
    AllowAgentForwarding no
    AllowTcpForwarding no
    X11Forwarding no
    ChrootDirectory /var/sftp/atm_logs
```
> ⚠️ *Règle stricte OpenSSH : Le dossier pointé par `ChrootDirectory` (`/var/sftp/atm_logs`) doit obligatoirement appartenir à `root:root` et avoir des permissions à `755`. L'utilisateur `atmagent` aura les droits d'écriture dans un sous-dossier (ex: `/var/sftp/atm_logs/upload`).*

---

## 4. Dimensionnement et Tolérance aux Pannes

### Concurrence
Le serveur SSH doit être capable d'accepter de multiples connexions simultanées. Chaque ATM établit et ferme une connexion SSH lors d'une transmission (généralement toutes les quelques minutes selon le trafic).
Dans `/etc/ssh/sshd_config`, ajustez la limite des sessions non authentifiées concurrentes si vous avez une flotte de milliers d'ATM qui démarrent en même temps :
```sshdconfig
MaxStartups 100:30:500
```

### Rétention et Stockage
* **Volumétrie** : Un log ATM quotidien compressé/textuel pèse environ 100 Ko à 1 Mo. Pour une flotte de 1000 ATM, prévoyez environ ~1 Go par jour, soit ~365 Go de stockage par an.
* Les systèmes de fichiers type ZFS ou LVM (XFS/ext4) avec des sauvegardes régulières (snapshots) sont recommandés.

---

## 5. Résumé des Actions pour la création du Serveur en Prod

1. Créer le groupe et l'utilisateur système sans shell :
   ```bash
   groupadd sftp_users
   useradd -m -d /home/atmagent -s /usr/sbin/nologin -g sftp_users atmagent
   ```
2. Mettre en place le dossier Chroot :
   ```bash
   mkdir -p /var/sftp/atm_logs/upload
   chown root:root /var/sftp/atm_logs
   chmod 755 /var/sftp/atm_logs
   chown atmagent:sftp_users /var/sftp/atm_logs/upload
   ```
3. Ajouter la clé publique de l'agent (`id_rsa.pub`) dans `/home/atmagent/.ssh/authorized_keys` et sécuriser les droits (`chmod 600`).
4. Configurer `sshd_config` (voir section 3.2) et redémarrer le service `systemctl restart sshd`.
5. Ouvrir le pare-feu (`ufw allow 22/tcp` ou via pare-feu matériel) de manière restrictive (uniquement pour les IPs/sous-réseaux ATM).
