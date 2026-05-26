using System.Text.RegularExpressions;
using AtmLogAgent.Core.Models;

namespace AtmLogAgent.Core.Parsers;

/// <summary>
/// Parseur spécialisé pour le format journal ATM propriétaire (.jrn).
/// Extrait les données structurées depuis les lignes brutes :
/// timestamps, codes réponse, montants, PAN masqués, statuts périphériques.
/// 
/// Exemples de formats supportés :
///   06:15:00 -> TRANSACTION START
///   *1000*06:00:02 OPERATOR DOOR OPENED
///   TRACK 2 DATA: 531234******5678
///   CODE REPONSE: 00
///   AMOUNT 30000 ENTERED
///   DEVICE CCCdmFW STATUS 0 SUPPLY 1
/// </summary>
public static class AtmJrnParser
{
    // Regex pré-compilées pour la performance (surveillance 24/7)
    private static readonly Regex TimestampRx =
        new(@"^(?:\*\d+\*)?(\d{2}):(\d{2}):(\d{2})", RegexOptions.Compiled);

    private static readonly Regex SystemEventRx =
        new(@"^\*(\d+)\*(\d{2}:\d{2}:\d{2})\s+(.+)$", RegexOptions.Compiled);

    private static readonly Regex TransactionStartRx =
        new(@"(\d{2}:\d{2}:\d{2})\s+->\s+TRANSACTION START", RegexOptions.Compiled);

    private static readonly Regex TransactionEndRx =
        new(@"<-\s+TRANSACTION END", RegexOptions.Compiled);

    private static readonly Regex ResponseCodeRx =
        new(@"CODE REPONSE[:\s]+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AmountRx =
        new(@"(?:MONTANT|AMOUNT)[:\s]+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrackDataRx =
        new(@"TRACK 2 DATA:\s*(\S+)", RegexOptions.Compiled);

    private static readonly Regex DeviceStatusRx =
        new(@"DEVICE\s+(\w+)\s+STATUS\s+(\d+)\s+SUPPLY\s+(\d+)", RegexOptions.Compiled);

    private static readonly Regex RrnRx =
        new(@"RRN[:\s]+(\w+)", RegexOptions.Compiled);

    private static readonly Regex EmvAidRx =
        new(@"EMV AID\s+([A-F0-9]+)", RegexOptions.Compiled);

    private static readonly Regex CassetteEventRx =
        new(@"(TOP|SECOND|THIRD|BOTTOM|REJECT)\s+CASSETTE\s+(INSERTED|REMOVED)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommunicationRx =
        new(@"COMMUNICATION\s+(ONLINE|OFFLINE)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CashCounterRx =
        new(@"CFA\s+(\d+)\s+(\d+)(\*?)", RegexOptions.Compiled);

    // ──────────────────────────────────────────────────────────
    // API publique
    // ──────────────────────────────────────────────────────────

    /// <summary>Extrait le timestamp d'une ligne de journal.</summary>
    public static TimeSpan? ExtractTimestamp(string line)
    {
        var m = TimestampRx.Match(line);
        if (!m.Success) return null;

        return new TimeSpan(
            int.Parse(m.Groups[1].Value),
            int.Parse(m.Groups[2].Value),
            int.Parse(m.Groups[3].Value));
    }

    /// <summary>Extrait le code réponse ISO 8583 (00=approuvé, 51=fonds insuf., etc.)</summary>
    public static string? ExtractResponseCode(string line)
    {
        var m = ResponseCodeRx.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Détermine si une transaction est approuvée (code 00).
    /// </summary>
    public static bool IsApproved(string responseCode) => responseCode == "00";

    /// <summary>Extrait le montant de la transaction.</summary>
    public static long? ExtractAmount(string line)
    {
        var m = AmountRx.Match(line);
        if (!m.Success) return null;
        return long.TryParse(m.Groups[1].Value, out var amount) ? amount : null;
    }

    /// <summary>Extrait le PAN masqué depuis une ligne TRACK 2.</summary>
    public static string? ExtractMaskedPan(string line)
    {
        var m = TrackDataRx.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Extrait le statut d'un périphérique ATM.</summary>
    public static DeviceStatusInfo? ExtractDeviceStatus(string line)
    {
        var m = DeviceStatusRx.Match(line);
        if (!m.Success) return null;

        return new DeviceStatusInfo(
            DeviceName: m.Groups[1].Value,
            Status: int.Parse(m.Groups[2].Value),
            Supply: int.Parse(m.Groups[3].Value));
    }

    /// <summary>Extrait le numéro de référence (RRN) de la transaction.</summary>
    public static string? ExtractRrn(string line)
    {
        var m = RrnRx.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Extrait l'AID EMV utilisé.</summary>
    public static string? ExtractEmvAid(string line)
    {
        var m = EmvAidRx.Match(line);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Détermine si la ligne représente un début de transaction.</summary>
    public static bool IsTransactionStart(string line) =>
        TransactionStartRx.IsMatch(line) || line.TrimStart().StartsWith("-> TRANSACTION START");

    /// <summary>Détermine si la ligne représente une fin de transaction.</summary>
    public static bool IsTransactionEnd(string line) => TransactionEndRx.IsMatch(line);

    /// <summary>Détermine si la ligne représente un événement système (format *NNN*).</summary>
    public static SystemEventInfo? ExtractSystemEvent(string line)
    {
        var m = SystemEventRx.Match(line);
        if (!m.Success) return null;

        return new SystemEventInfo(
            EventId: int.Parse(m.Groups[1].Value),
            Time: m.Groups[2].Value,
            Description: m.Groups[3].Value.Trim());
    }

    /// <summary>Extrait l'événement lié à une cassette (insertion/retrait).</summary>
    public static CassetteEventInfo? ExtractCassetteEvent(string line)
    {
        var m = CassetteEventRx.Match(line);
        if (!m.Success) return null;

        return new CassetteEventInfo(
            CassetteId: m.Groups[1].Value.ToUpperInvariant(),
            Action: m.Groups[2].Value.ToUpperInvariant());
    }

    /// <summary>Détecte les événements réseau (ONLINE/OFFLINE).</summary>
    public static string? ExtractCommunicationStatus(string line)
    {
        var m = CommunicationRx.Match(line);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Extrait les données des compteurs de billets.
    /// Format : "   CFA    10000 1553*" (étoile = estimation avant SOP)
    /// </summary>
    public static CashCounterInfo? ExtractCashCounter(string line)
    {
        var m = CashCounterRx.Match(line);
        if (!m.Success) return null;

        return new CashCounterInfo(
            Denomination: int.Parse(m.Groups[1].Value),
            Count: int.Parse(m.Groups[2].Value),
            IsEstimate: m.Groups[3].Value == "*");
    }

    /// <summary>
    /// Classifie une ligne de journal selon son type.
    /// Utile pour le routage vers des règles d'alerte différentes.
    /// </summary>
    public static JrnLineType ClassifyLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))            return JrnLineType.Empty;
        if (IsTransactionStart(line))                   return JrnLineType.TransactionStart;
        if (IsTransactionEnd(line))                     return JrnLineType.TransactionEnd;
        if (ExtractSystemEvent(line) is not null)       return JrnLineType.SystemEvent;
        if (DeviceStatusRx.IsMatch(line))               return JrnLineType.DeviceStatus;
        if (TrackDataRx.IsMatch(line))                  return JrnLineType.TrackData;
        if (ResponseCodeRx.IsMatch(line))               return JrnLineType.ResponseCode;
        if (AmountRx.IsMatch(line))                     return JrnLineType.Amount;
        if (CassetteEventRx.IsMatch(line))              return JrnLineType.CassetteEvent;
        if (CommunicationRx.IsMatch(line))              return JrnLineType.CommunicationEvent;
        if (CashCounterRx.IsMatch(line))                return JrnLineType.CashCounter;
        if (line.Contains("EMV AID"))                   return JrnLineType.EmvEvent;
        if (line.Contains("PIN"))                       return JrnLineType.PinEvent;
        if (line.Contains("CARD RETAINED")
            || line.Contains("CARTE BLOQUEE"))          return JrnLineType.CardRetained;
        return JrnLineType.Other;
    }

    /// <summary>
    /// Analyse un bloc de transaction complet et retourne les données structurées.
    /// </summary>
    public static TransactionSummary? ParseTransactionBlock(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return null;

        var summary = new TransactionSummary();

        foreach (var line in lines)
        {
            var type = ClassifyLine(line);
            switch (type)
            {
                case JrnLineType.ResponseCode:
                    summary.ResponseCode = ExtractResponseCode(line);
                    break;
                case JrnLineType.Amount:
                    summary.Amount = ExtractAmount(line);
                    break;
                case JrnLineType.TrackData:
                    summary.MaskedPan = ExtractMaskedPan(line);
                    break;
            }

            if (line.Contains("RRN"))        summary.Rrn = ExtractRrn(line);
            if (line.Contains("CASH TAKEN")) summary.CashTaken = true;
            if (line.Contains("CARD RETAINED") || line.Contains("CARTE BLOQUEE"))
                summary.CardRetained = true;
            if (line.Contains("USER CANCELLED")) summary.UserCancelled = true;
            if (line.Contains("ERREUR SYSTEME")) summary.SystemError = true;
        }

        return summary;
    }

    // ──────────────────────────────────────────────
    // P3.3 — Détection EOP/SOP (End/Start Of Period)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Détecte les événements de période comptable bancaire dans une ligne de log.
    ///
    /// SOP (Start Of Period / CASH COUNTERS AFTER SOP) :
    ///   Déclenché après le rechargement des cassettes. Représente le début de
    ///   la nouvelle période comptable avec les compteurs remis à zéro.
    ///   → Déclenche une synchronisation complète prioritaire.
    ///
    /// EOP (End Of Period / CASH COUNTERS BEFORE SOP) :
    ///   Derniers compteurs avant le rechargement. Représente la clôture de la
    ///   période précédente avec l'état exact des cassettes avant vidage.
    ///   → Doit être transmis en priorité pour les rapports Back-Office.
    /// </summary>
    public static PeriodicEventInfo? DetectPeriodicEvent(string line)
    {
        // EOP : "CASH COUNTERS BEFORE SOP" = fin de la période précédente
        if (line.Contains("CASH COUNTERS BEFORE SOP", StringComparison.OrdinalIgnoreCase))
            return new PeriodicEventInfo(PeriodicEventType.Eop, line);

        // SOP : "CASH COUNTERS AFTER SOP" = début de la nouvelle période
        if (line.Contains("CASH COUNTERS AFTER SOP", StringComparison.OrdinalIgnoreCase))
            return new PeriodicEventInfo(PeriodicEventType.Sop, line);

        return null;
    }

    // ──────────────────────────────────────────────
    // P3.4 — Journal Électronique (EJ) séquentiel
    // ──────────────────────────────────────────────

    /// <summary>
    /// Construit une entrée EJ numérotée à partir d'un bloc de transaction.
    /// L'EJ séquentiel est obligatoire selon les réglementations CEMAC et UEMOA :
    /// chaque transaction doit être numérotée séquentiellement et immuablement.
    ///
    /// Le numéro EJ (ejSequenceNumber) doit être maintenu par le service appelant
    /// et incrémenté atomiquement pour chaque transaction traitée.
    /// </summary>
    public static ElectronicJournalEntry? BuildElectronicJournalEntry(
        IReadOnlyList<string> transactionLines,
        int ejSequenceNumber,
        string atmId,
        string sourceFile)
    {
        if (transactionLines.Count == 0) return null;

        var summary = ParseTransactionBlock(transactionLines);
        if (summary is null) return null;

        return new ElectronicJournalEntry
        {
            SequenceNumber = ejSequenceNumber,
            AtmId          = atmId,
            SourceFile     = sourceFile,
            Content        = string.Join(Environment.NewLine, transactionLines),
            ResponseCode   = summary.ResponseCode,
            MaskedPan      = summary.MaskedPan,
            Amount         = summary.Amount,
            Rrn            = summary.Rrn,
        };
    }

    // ──────────────────────────────────────────────
    // P3.5 — Rapport de distribution billets
    // ──────────────────────────────────────────────

    /// <summary>
    /// Construit un rapport de distribution billets à partir d'un fichier .jrn complet.
    /// Analyse les sections CASH COUNTERS BEFORE SOP et AFTER SOP pour calculer
    /// le nombre de billets distribués par coupure (Back-Office bancaire).
    ///
    /// Exemple réel (20230418.jrn) :
    ///   BEFORE SOP : CFA 10000 × 1553 + CFA 5000 × 290 + ...
    ///   AFTER SOP  : CFA 10000 × 853  + CFA 5000 × 1003 + ...
    ///   → Distribué : 10000 × 700 + ... (différence = billets sortis)
    /// </summary>
    public static CashSummary BuildCashSummary(IEnumerable<string> lines)
    {
        var summary = new CashSummary();
        var inBeforeSop = false;
        var inAfterSop  = false;

        foreach (var line in lines)
        {
            if (line.Contains("CASH COUNTERS BEFORE SOP", StringComparison.OrdinalIgnoreCase))
            {
                inBeforeSop = true;
                inAfterSop  = false;
                continue;
            }
            if (line.Contains("CASH COUNTERS AFTER SOP", StringComparison.OrdinalIgnoreCase))
            {
                inBeforeSop = false;
                inAfterSop  = true;
                continue;
            }

            // Quitter la section si on rencontre une ligne vide ou un autre marqueur
            if (string.IsNullOrWhiteSpace(line) && (inBeforeSop || inAfterSop))
            {
                // Ne pas quitter trop tôt : les compteurs peuvent être séparés par des espaces
                continue;
            }

            var counter = ExtractCashCounter(line);
            if (counter is null) continue;

            if (inBeforeSop) summary.BeforeSop.Add(counter);
            if (inAfterSop)  summary.AfterSop.Add(counter);
        }

        return summary;
    }
}

// ── Types de résultats ────────────────────────────────────────

public record DeviceStatusInfo(string DeviceName, int Status, int Supply)
{
    // P3.1 — Mapping XFS WFS_STAT_DEV_* (CEN/XFS SP 3.x)
    public bool IsOnline     => Status == XfsDeviceStatus.Online;
    public bool IsOffline    => Status == XfsDeviceStatus.Offline;
    public bool IsPoweredOff => Status == XfsDeviceStatus.PoweredOff;
    public bool IsNoDevice   => Status == XfsDeviceStatus.NoDevice;
    public bool IsHwError    => Status == XfsDeviceStatus.HwError;
    public bool IsUserError  => Status == XfsDeviceStatus.UserError;
    public bool IsError      => Status != XfsDeviceStatus.Online;
    public bool HasSupply    => Supply == 1;
    public string XfsStatusDescription => XfsDeviceStatus.GetDescription(Status);
}

public record SystemEventInfo(int EventId, string Time, string Description);

public record CassetteEventInfo(string CassetteId, string Action)
{
    public bool IsInsertion => Action == "INSERTED";
    public bool IsRemoval   => Action == "REMOVED";
}

public record CashCounterInfo(int Denomination, int Count, bool IsEstimate)
{
    public long TotalValue => (long)Denomination * Count;
}

/// <summary>
/// Bilan d'une transaction journal.
/// </summary>
public sealed class TransactionSummary
{
    public string? MaskedPan     { get; set; }
    public string? ResponseCode  { get; set; }
    public long?   Amount        { get; set; }
    public string? Rrn           { get; set; }
    public bool    CashTaken     { get; set; }
    public bool    CardRetained  { get; set; }
    public bool    UserCancelled { get; set; }
    public bool    SystemError   { get; set; }
    public bool    IsApproved    => ResponseCode == "00";
    public bool    IsSuspicious  => CardRetained && IsApproved;

    /// <summary>P3.2 — Signification ISO 8583 du code réponse.</summary>
    public string ResponseCodeDescription =>
        Iso8583ResponseCode.GetDescription(ResponseCode);
}

// ─────────────────────────────────────────────────────────────────
//  P3.1 — Mapping XFS CEN/XFS WFS_STAT_DEV_* (CEN/XFS SP 3.x)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Codes de statut XFS pour les périphériques ATM.
/// Standard CEN/XFS (Extension for Financial Services) utilisé par tous les fabricants.
/// </summary>
public static class XfsDeviceStatus
{
    /// <summary>WFS_STAT_DEVONLINE (0) — Périphérique opérationnel.</summary>
    public const int Online    = 0;
    /// <summary>WFS_STAT_DEVOFFLINE (1) — Périphérique hors ligne (désactivé logiciellement).</summary>
    public const int Offline   = 1;
    /// <summary>WFS_STAT_DEVPOWEROFF (2) — Périphérique éteint.</summary>
    public const int PoweredOff = 2;
    /// <summary>WFS_STAT_DEVNODEVICE (3) — Aucun périphérique détecté (absent).</summary>
    public const int NoDevice  = 3;
    /// <summary>WFS_STAT_DEVHWERROR (4) — Erreur matérielle (jam, capteur défaillant...).</summary>
    public const int HwError   = 4;
    /// <summary>WFS_STAT_DEVUSERERROR (5) — Erreur utilisateur (bourrage, mauvaise insertion).</summary>
    public const int UserError = 5;
    /// <summary>WFS_STAT_DEVBUSY (6) — Périphérique occupé (traitement en cours).</summary>
    public const int Busy      = 6;
    /// <summary>WFS_STAT_DEVFRAUDATTEMPT (7) — Tentative de fraude détectée.</summary>
    public const int FraudAttempt = 7;
    /// <summary>WFS_STAT_DEVPOTENTIALFRAUD (8) — Fraude potentielle.</summary>
    public const int PotentialFraud = 8;

    private static readonly Dictionary<int, string> Descriptions = new()
    {
        [Online]        = "Online (WFS_STAT_DEVONLINE)",
        [Offline]       = "Offline (WFS_STAT_DEVOFFLINE)",
        [PoweredOff]    = "Powered off (WFS_STAT_DEVPOWEROFF)",
        [NoDevice]      = "No device (WFS_STAT_DEVNODEVICE)",
        [HwError]       = "Hardware error (WFS_STAT_DEVHWERROR)",
        [UserError]     = "User error (WFS_STAT_DEVUSERERROR)",
        [Busy]          = "Busy (WFS_STAT_DEVBUSY)",
        [FraudAttempt]  = "Fraud attempt (WFS_STAT_DEVFRAUDATTEMPT)",
        [PotentialFraud]= "Potential fraud (WFS_STAT_DEVPOTENTIALFRAUD)",
    };

    public static string GetDescription(int statusCode) =>
        Descriptions.TryGetValue(statusCode, out var desc) ? desc : $"Unknown ({statusCode})";
}

// ─────────────────────────────────────────────────────────────────
//  P3.2 — Codes réponse ISO 8583 complets
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// Codes réponse ISO 8583 pertinents pour les transactions ATM.
/// Source : ISO 8583:2003 + extensions régionales VISA/Mastercard.
/// </summary>
public static class Iso8583ResponseCode
{
    public const string Approved           = "00"; // Transaction approuvée
    public const string DoNotHonor         = "05"; // Refus banque émettrice (générique)
    public const string InvalidCardNumber  = "14"; // Numéro de carte invalide
    public const string InsufficientFunds  = "51"; // Fonds insuffisants
    public const string ExpiredCard        = "54"; // Carte expirée
    public const string TxNotPermitted     = "57"; // Transaction non autorisée sur ce terminal
    public const string PinTriesExceeded   = "75"; // PIN incorrect — 3 essais dépassés
    public const string IssuerUnavailable  = "91"; // Banque émettrice inaccessible (timeout réseau interbancaire)
    public const string SystemMalfunction  = "96"; // Panne système (switch bancaire, réseau)

    private static readonly Dictionary<string, string> Descriptions = new()
    {
        [Approved]          = "Transaction approuvée",
        [DoNotHonor]        = "Refus banque émettrice (do not honor)",
        [InvalidCardNumber] = "Numéro de carte invalide",
        [InsufficientFunds] = "Fonds insuffisants",
        [ExpiredCard]       = "Carte expirée",
        [TxNotPermitted]    = "Transaction non autorisée sur ce terminal",
        [PinTriesExceeded]  = "Nombre d'essais PIN dépassé — carte bloquée",
        [IssuerUnavailable] = "Banque émettrice inaccessible (infrastructure interbancaire)",
        [SystemMalfunction] = "Panne système (switch / réseau bancaire)",
    };

    public static string GetDescription(string? code) =>
        code is not null && Descriptions.TryGetValue(code, out var desc)
            ? desc
            : $"Code inconnu ({code ?? "null"})";;

    /// <summary>Vrai si le code indique un problème d'infrastructure interbancaire
    /// (pas un problème de l'ATM lui-même).</summary>
    public static bool IsInfrastructureError(string? code) =>
        code is IssuerUnavailable or SystemMalfunction;

    /// <summary>Vrai si le code indique un problème de carte (refus, expirée, bloquée).</summary>
    public static bool IsCardError(string? code) =>
        code is DoNotHonor or InvalidCardNumber or ExpiredCard
                or TxNotPermitted or PinTriesExceeded;
}

// ─────────────────────────────────────────────────────────────────
//  P3.3 — Détection EOP/SOP (End/Start Of Period)
//  P3.4 — Journal électronique (EJ) séquentiel
//  P3.5 — Rapport de distribution billets (CashSummary)
// ─────────────────────────────────────────────────────────────────

/// <summary>
/// P3.3 — Type d'un événement de période comptable ATM.
/// SOP = Start Of Period = début de journée bancaire (ouverture cassettes).
/// EOP = End Of Period = clôture journée bancaire (bilan de distributions).
/// </summary>
public enum PeriodicEventType { None, Sop, Eop }

public record PeriodicEventInfo(PeriodicEventType EventType, string Line);

/// <summary>
/// P3.4 — Enregistrement d'une entrée du journal électronique (EJ).
/// L'EJ est une séquence numérotée immuable de toutes les transactions,
/// obligatoire selon les réglementations bancaires CEMAC et UEMOA.
/// </summary>
public sealed class ElectronicJournalEntry
{
    public required int    SequenceNumber { get; init; }  // Numéro EJ séquentiel
    public required string AtmId          { get; init; }
    public required string SourceFile     { get; init; }
    public required string Content        { get; init; }  // Lignes brutes du bloc transaction
    public string? ResponseCode           { get; init; }
    public string? MaskedPan              { get; init; }
    public long?   Amount                 { get; init; }
    public string? Rrn                    { get; init; }
    public bool    IsApproved             => ResponseCode == "00";
    public DateTime Timestamp             { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// P3.5 — Récapitulatif de distribution de billets depuis les compteurs cassettes.
/// Produit à partir des sections CASH COUNTERS BEFORE/AFTER SOP.
/// </summary>
public sealed class CashSummary
{
    public List<CashCounterInfo> BeforeSop { get; } = [];
    public List<CashCounterInfo> AfterSop  { get; } = [];

    /// <summary>Total de billets distribués (BEFORE.Count - AFTER.Count par coupure).</summary>
    public IEnumerable<(int Denomination, int Dispensed)> DispensedByDenomination
    {
        get
        {
            var beforeByDenom = BeforeSop
                .Where(c => !c.IsEstimate)  // on préfère les valeurs exactes
                .GroupBy(c => c.Denomination)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.Count));

            var afterByDenom = AfterSop
                .GroupBy(c => c.Denomination)
                .ToDictionary(g => g.Key, g => g.Sum(c => c.Count));

            foreach (var (denom, beforeCount) in beforeByDenom)
            {
                if (afterByDenom.TryGetValue(denom, out var afterCount))
                    yield return (denom, beforeCount - afterCount);
            }
        }
    }

    /// <summary>Montant total distribué en XAF (ou devise de l'ATM).</summary>
    public long TotalDispensed => DispensedByDenomination
        .Sum(d => (long)d.Denomination * d.Dispensed);
}

public enum JrnLineType
{
    Empty,
    TransactionStart,
    TransactionEnd,
    SystemEvent,
    DeviceStatus,
    TrackData,
    ResponseCode,
    Amount,
    CassetteEvent,
    CommunicationEvent,
    CashCounter,
    EmvEvent,
    PinEvent,
    CardRetained,
    Other
}
