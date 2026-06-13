#!/bin/bash
# ═══════════════════════════════════════════════════════════════
#  ATM Log Agent — Script d'installation Linux (systemd)
#  Exécuter en tant que root : sudo bash install.sh [options]
# ═══════════════════════════════════════════════════════════════

set -euo pipefail

# ── Paramètres par défaut ───────────────────────────────────
SERVICE_NAME="atm-log-agent"
INSTALL_DIR="/opt/atm-log-agent"
DATA_DIR="/var/lib/atm-log-agent"
LOG_DIR="/var/log/atm-log-agent"
SERVICE_USER="atm-agent"

BANK_NAME="${ATM_BANK:-AUTO}"
COUNTRY="${ATM_COUNTRY:-AUTO}"
CITY="${ATM_CITY:-AUTO}"
ATM_ID="${ATM_ID:-AUTO}"
SFTP_HOST="${ATM_SFTP_HOST:-sftp.banque.example.com}"
SFTP_PORT="${ATM_SFTP_PORT:-22}"
SFTP_USER="${ATM_SFTP_USER:-atm-agent}"
SFTP_HOSTKEY="${ATM_SFTP_HOSTKEY:-}"
HEARTBEAT_URL="${ATM_HEARTBEAT_URL:-https://supervision.example.com/api/heartbeat}"

# Couleurs
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; CYAN='\033[0;36m'; NC='\033[0m'

log_info()    { echo -e "${GREEN}  [✓]${NC} $1"; }
log_warn()    { echo -e "${YELLOW}  [!]${NC} $1"; }
log_error()   { echo -e "${RED}  [✗]${NC} $1"; }
log_section() { echo -e "\n${CYAN}[$1]${NC} $2"; }

echo -e "${CYAN}═══════════════════════════════════════════════${NC}"
echo -e "${CYAN}  ATM Log Agent — Installation Linux${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════${NC}"

# ── Vérifications ───────────────────────────────────────────
log_section "1/8" "Vérification de l'environnement"

if [[ $EUID -ne 0 ]]; then
    log_error "Ce script doit être exécuté en tant que root (sudo)"
    exit 1
fi

if ! command -v dotnet &>/dev/null; then
    log_error ".NET Runtime non trouvé. Installation requise :"
    echo "         wget https://dot.net/v1/dotnet-install.sh && bash dotnet-install.sh --runtime aspnetcore --version 8.0"
    exit 1
fi

if [[ -z "$SFTP_HOSTKEY" ]]; then
    log_error "ATM_SFTP_HOSTKEY est obligatoire en production (fingerprint SHA-256/MD5 hex normalisé de la clé serveur SSH)."
    exit 1
fi

DOTNET_VERSION=$(dotnet --version)
log_info ".NET Runtime : $DOTNET_VERSION"

# ── Utilisateur système dédié ───────────────────────────────
log_section "2/8" "Création de l'utilisateur système"

if ! id "$SERVICE_USER" &>/dev/null; then
    useradd --system --no-create-home --shell /bin/false \
            --comment "ATM Log Agent Service" "$SERVICE_USER"
    log_info "Utilisateur '$SERVICE_USER' créé"
else
    log_info "Utilisateur '$SERVICE_USER' existant conservé"
fi

# ── Répertoires ─────────────────────────────────────────────
log_section "3/8" "Création des répertoires"

mkdir -p "$INSTALL_DIR" "$DATA_DIR/keys" "$DATA_DIR/backups" "$LOG_DIR"

chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_DIR" "$LOG_DIR"
chmod 750 "$DATA_DIR" "$LOG_DIR"
chmod 700 "$DATA_DIR/keys"

log_info "Répertoires créés et sécurisés"

# ── Copie des binaires ──────────────────────────────────────
log_section "4/8" "Installation des binaires"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cp -r "$SCRIPT_DIR"/bin/* "$INSTALL_DIR/" 2>/dev/null || \
    cp -r ./* "$INSTALL_DIR/" 2>/dev/null || true

chown -R root:"$SERVICE_USER" "$INSTALL_DIR"
chmod -R 550 "$INSTALL_DIR"
chmod 750 "$INSTALL_DIR/AtmLogAgent.Service"

log_info "Binaires installés dans $INSTALL_DIR"

# ── Configuration ───────────────────────────────────────────
log_section "5/8" "Génération de la configuration"

cat > "$INSTALL_DIR/appsettings.json" <<EOF
{
  "AtmAgent": {
    "Atm": {
      "BankName": "$BANK_NAME",
      "Country": "$COUNTRY",
      "City": "$CITY",
      "AtmId": "$ATM_ID",
      "Manufacturer": "AUTO"
    },
    "Transmission": {
      "Protocol": "SFTP",
      "Host": "$SFTP_HOST",
      "Port": $SFTP_PORT,
      "Username": "$SFTP_USER",
      "PrivateKeyPath": "$DATA_DIR/keys/agent_ed25519",
      "CompressBeforeTransmit": true,
      "MaxRetryAttempts": 10,
      "RetryDelaySeconds": 30,
      "FullSyncIntervalHours": 24
    },
    "Security": {
      "LocalEncryptionKeyId": "$DATA_DIR/agent.key",
      "EnableIntegrityChecks": true,
      "EnableTamperDetection": true,
      "ValidateServerCertificate": true,
      "ServerCertificatePinning": "$SFTP_HOSTKEY",
      "EnableAuditLog": true,
      "AuditLogPath": "$LOG_DIR/audit.log"
    },
    "LogDiscovery": {
      "WatchPaths": [],
      "FilePatterns": ["*.jrn", "*.log", "*.txt", "*.xml", "*.json"],
      "AutoDiscoverAtmPaths": true,
      "IncludeSubdirectories": true,
      "ExcludedPaths": ["$DATA_DIR", "/proc", "/sys"]
    },
    "Update": {
      "UpdateServerUrl": "https://updates.atm-agent.example.com/api/v1",
      "UpdatePublicKeyPath": "$DATA_DIR/keys/update_pub.pem",
      "CheckIntervalHours": 6,
      "EnableAutoUpdate": false,
      "AllowHotReload": false,
      "MaxRollbackVersions": 3
    },
    "Monitoring": {
      "HeartbeatUrl": "$HEARTBEAT_URL",
      "HeartbeatIntervalSeconds": 60
    },
    "Retention": {
      "LocalLogRetentionDays": 30,
      "BufferedDataRetentionDays": 7,
      "MaxLocalBufferSizeMb": 500
    }
  }
}
EOF

chmod 640 "$INSTALL_DIR/appsettings.json"
chown root:"$SERVICE_USER" "$INSTALL_DIR/appsettings.json"
log_info "Configuration générée"

# ── Clé SSH ─────────────────────────────────────────────────
log_section "6/8" "Génération de la clé SSH"

KEY_PATH="$DATA_DIR/keys/agent_ed25519"
if [[ ! -f "$KEY_PATH" ]]; then
    sudo -u "$SERVICE_USER" ssh-keygen -t ed25519 \
        -f "$KEY_PATH" -N "" -C "atm-agent-$ATM_ID" -q
    chmod 600 "$KEY_PATH"
    chmod 644 "$KEY_PATH.pub"
    log_info "Clé ED25519 générée"
    echo ""
    echo -e "  ${YELLOW}╔══════════════════════════════════════════════╗${NC}"
    echo -e "  ${YELLOW}║  ACTION REQUISE : Ajouter la clé publique    ║${NC}"
    echo -e "  ${YELLOW}║  dans ~/.ssh/authorized_keys sur $SFTP_HOST  ║${NC}"
    echo -e "  ${YELLOW}╚══════════════════════════════════════════════╝${NC}"
    echo ""
    echo "  Clé publique :"
    cat "$KEY_PATH.pub"
    echo ""
else
    log_info "Clé existante conservée"
fi

# ── Service systemd ─────────────────────────────────────────
log_section "7/8" "Création du service systemd"

cat > "/etc/systemd/system/$SERVICE_NAME.service" <<EOF
[Unit]
Description=ATM Log Agent - $BANK_NAME $ATM_ID
Documentation=https://github.com/votre-org/atm-log-agent
After=network-online.target
Wants=network-online.target
StartLimitIntervalSec=300
StartLimitBurst=5

[Service]
Type=notify
User=$SERVICE_USER
Group=$SERVICE_USER
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/AtmLogAgent.Service
Restart=always
RestartSec=10
TimeoutStartSec=30
TimeoutStopSec=30

# Environnement
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ATMAGENT_DATA_DIR=$DATA_DIR

# Journaux
StandardOutput=journal
StandardError=journal
SyslogIdentifier=$SERVICE_NAME

# Sécurité (sandboxing)
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=$DATA_DIR $LOG_DIR
CapabilityBoundingSet=
AmbientCapabilities=
SecureBits=noroot
MemoryDenyWriteExecute=yes
RestrictRealtime=yes
RestrictNamespaces=yes
SystemCallFilter=@system-service
SystemCallErrorNumber=EPERM

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
log_info "Service systemd créé et activé"

# ── Démarrage ───────────────────────────────────────────────
log_section "8/8" "Démarrage du service"

systemctl start "$SERVICE_NAME"
sleep 3

STATUS=$(systemctl is-active "$SERVICE_NAME" 2>/dev/null || echo "unknown")
if [[ "$STATUS" == "active" ]]; then
    log_info "Service démarré avec succès !"
else
    log_warn "Statut du service : $STATUS"
    echo "  Consulter les logs : journalctl -u $SERVICE_NAME -f"
fi

# ── Résumé ──────────────────────────────────────────────────
echo ""
echo -e "${CYAN}═══════════════════════════════════════════════${NC}"
echo -e "${GREEN}  Installation terminée !${NC}"
echo -e "${CYAN}═══════════════════════════════════════════════${NC}"
echo ""
echo "  ATM          : $BANK_NAME / $COUNTRY / $CITY / $ATM_ID"
echo "  Service      : $SERVICE_NAME ($STATUS)"
echo "  Installation : $INSTALL_DIR"
echo "  Données      : $DATA_DIR"
echo "  Logs agent   : journalctl -u $SERVICE_NAME -f"
echo "  Audit        : $LOG_DIR/audit.log"
echo ""
echo "  Commandes utiles :"
echo "    systemctl status $SERVICE_NAME"
echo "    systemctl restart $SERVICE_NAME"
echo "    journalctl -u $SERVICE_NAME -f"
echo ""
