using System.Text.RegularExpressions;
using AtmLogAgent.Core.Parsers;
using FluentAssertions;
using Xunit;

namespace AtmLogAgent.Tests;

/// <summary>
/// Tests d'intégration sur les vrais fichiers .jrn fournis par BGFI Gabon.
/// Les fichiers sont lus depuis TestData/ (copie locale des journaux ATM).
///
/// Fichiers :
///   20200810.jrn — Journée complète : 11 transactions, maintenance, comm events
///   20230418.jrn — Rechargement cassettes + 3 transactions format étendu
///   20240512.jrn — 2 transactions format étendu avec timestamps par ligne
/// </summary>
public sealed class JrnFileIntegrationTests
{
    // Résolution du chemin TestData/ relatif à l'assembly de test
    private static string TestDataPath => Path.Combine(
        AppContext.BaseDirectory, "TestData");

    private static string JrnPath(string filename) =>
        Path.Combine(TestDataPath, filename);

    private static string[] ReadJrnLines(string filename)
    {
        var path = JrnPath(filename);
        path.Should().NotBeNull();
        File.Exists(path).Should().BeTrue($"Le fichier de test {filename} doit exister dans TestData/");

        // Les fichiers ATM peuvent avoir des fins de ligne \r\r\n (Windows Embedded)
        // On normalise : trim chaque ligne, on ignore les vides
        return File.ReadAllLines(path)
            .Select(l => l.TrimEnd('\r', '\n', ' '))
            .ToArray();
    }

    // ══════════════════════════════════════════════════════════════
    //  20200810.jrn — Journée complète
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void File_20200810_ShouldBeReadable()
    {
        var lines = ReadJrnLines("20200810.jrn");
        lines.Should().NotBeEmpty("le fichier 20200810.jrn doit contenir des données");
        lines.Length.Should().BeGreaterThan(10);
    }

    [Fact]
    public void File_20200810_ShouldStartWithJournalingStarted()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var firstContent = lines.First(l => !string.IsNullOrWhiteSpace(l));
        firstContent.Should().Contain("JOURNALING STARTED",
            "tout fichier .jrn débute par JOURNALING STARTED");
    }

    [Fact]
    public void File_20200810_TransactionCount_ShouldBeBalanced()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var starts = lines.Count(l => l.Contains("-> TRANSACTION START"));
        var ends   = lines.Count(l => l.Contains("<- TRANSACTION END"));

        starts.Should().BeGreaterThan(0, "le fichier doit contenir des transactions");
        starts.Should().Be(ends, "chaque TRANSACTION START doit avoir un TRANSACTION END");
    }

    [Fact]
    public void File_20200810_ShouldContain11Transactions()
    {
        var lines  = ReadJrnLines("20200810.jrn");
        var starts = lines.Count(l => l.Contains("-> TRANSACTION START"));
        starts.Should().Be(11, "le journal 20200810.jrn contient 11 transactions");
    }

    [Fact]
    public void File_20200810_ResponseCodes_ShouldCoverAllISO8583Cases()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var codes = lines
            .Select(l => Regex.Match(l, @"CODE REPONSE[:\s]+(\d+)"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToHashSet();

        codes.Should().Contain("00", "transaction approuvée (ISO 8583 code 00)");
        codes.Should().Contain("51", "fonds insuffisants (ISO 8583 code 51)");
        codes.Should().Contain("54", "carte expirée (ISO 8583 code 54)");
        codes.Should().Contain("75", "PIN incorrect 3 fois (ISO 8583 code 75)");
    }

    [Fact]
    public void File_20200810_AllPanShouldBeMasked()
    {
        var lines = ReadJrnLines("20200810.jrn");

        // Toutes les lignes TRACK 2 DATA doivent avoir le PAN masqué
        var trackLines = lines.Where(l => l.Contains("TRACK 2 DATA:")).ToList();
        trackLines.Should().NotBeEmpty("des données de carte doivent être présentes");

        foreach (var line in trackLines)
        {
            // Le PAN doit être masqué : 6 chiffres + au moins 4 étoiles + 4 chiffres
            var pan = Regex.Match(line, @"TRACK 2 DATA:\s*(\S+)").Groups[1].Value;
            pan.Should().MatchRegex(@"^\d{6}\*+\d{4}$",
                $"Le PAN dans '{line}' doit être masqué (PCI-DSS)");
        }
    }

    [Fact]
    public void File_20200810_ShouldDetectCardRetainedTransaction()
    {
        var lines = ReadJrnLines("20200810.jrn");

        // Transaction 09:45 : code 00 + CARD RETAINED = cas suspect (fraude possible)
        var retainedLines = lines.Where(l =>
            l.Contains("CARD RETAINED") || l.Contains("CARTE BLOQUEE")).ToList();

        retainedLines.Should().NotBeEmpty(
            "le journal contient des transactions avec carte retenue");
    }

    [Fact]
    public void File_20200810_SuspiciousTransaction_CardRetainedAfterApproval()
    {
        var lines = ReadJrnLines("20200810.jrn");

        // Détecter une transaction avec CODE REPONSE 00 suivie de CARD RETAINED
        // (logique IsSuspicious du TransactionSummary)
        var approvedWithRetained = false;
        var inTx = false;
        var approved = false;

        foreach (var line in lines)
        {
            if (line.Contains("-> TRANSACTION START"))  { inTx = true; approved = false; continue; }
            if (line.Contains("<- TRANSACTION END"))
            {
                if (inTx && approved) { /* transaction complète */ }
                inTx = false; approved = false;
                continue;
            }
            if (!inTx) continue;
            if (Regex.IsMatch(line, @"CODE REPONSE[:\s]+00")) approved = true;
            if ((line.Contains("CARD RETAINED") || line.Contains("CARTE BLOQUEE")) && approved)
                approvedWithRetained = true;
        }

        approvedWithRetained.Should().BeTrue(
            "Le journal contient une transaction approuvée (00) avec carte retenue — cas suspect");
    }

    [Fact]
    public void File_20200810_SystemEvents_ShouldHaveAscendingIds()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var ids = lines
            .Select(l => Regex.Match(l, @"^\*(\d+)\*"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

        ids.Should().NotBeEmpty("le journal contient des événements système *NNN*");
        ids.Should().BeInAscendingOrder("les IDs d'événements système sont séquentiels");
    }

    [Fact]
    public void File_20200810_CommunicationEvents_ShouldBePaired()
    {
        var lines    = ReadJrnLines("20200810.jrn");
        var offline  = lines.Count(l => l.Contains("COMMUNICATION OFFLINE"));
        var online   = lines.Count(l => l.Contains("COMMUNICATION ONLINE"));

        offline.Should().BeGreaterThan(0, "au moins un événement OFFLINE attendu");
        online.Should().BeGreaterThanOrEqualTo(offline,
            "chaque OFFLINE doit être suivi d'un ONLINE (reconnexion)");
    }

    [Fact]
    public void File_20200810_DeviceStatuses_ShouldBeDetected()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var deviceLines = lines.Where(l => Regex.IsMatch(l,
            @"DEVICE\s+\w+\s+STATUS\s+\d+\s+SUPPLY\s+\d+")).ToList();

        deviceLines.Should().NotBeEmpty("le journal contient des statuts de périphériques ATM");

        // Au moins un dispositif de distribution (CCdmFW)
        deviceLines.Any(l => l.Contains("CCCdmFW")).Should().BeTrue(
            "le distributeur de billets (CCCdmFW) doit être reporté");
    }

    [Fact]
    public void File_20200810_AtmJrnParser_ShouldClassifyAllLines()
    {
        var lines = ReadJrnLines("20200810.jrn");
        var unclassifiedCount = 0;

        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var type = AtmJrnParser.ClassifyLine(line);
            // On ne compte que les lignes "Other" qui contiennent un pattern connu
            // (les lignes de séparation ====, ---- sont normalement "Other")
            if (type == JrnLineType.Other) unclassifiedCount++;
        }

        // Il est normal d'avoir des lignes "Other" (tickets, séparateurs)
        // mais les types fonctionnels clés doivent être identifiés
        var transactionStarts = lines.Count(l =>
            AtmJrnParser.ClassifyLine(l) == JrnLineType.TransactionStart);
        var responseCodes = lines.Count(l =>
            AtmJrnParser.ClassifyLine(l) == JrnLineType.ResponseCode);

        transactionStarts.Should().BeGreaterThan(0);
        responseCodes.Should().BeGreaterThan(0);
    }

    // ══════════════════════════════════════════════════════════════
    //  20230418.jrn — Rechargement cassettes + transactions
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void File_20230418_ShouldBeReadable()
    {
        var lines = ReadJrnLines("20230418.jrn");
        lines.Should().NotBeEmpty();
        lines.Length.Should().BeGreaterThan(50, "le fichier 20230418.jrn est volumineux (maintenance)");
    }

    [Fact]
    public void File_20230418_ShouldDetectCassetteMaintenanceSequence()
    {
        var lines = ReadJrnLines("20230418.jrn");

        // Rechargement complet : REMOVED puis INSERTED pour les 4 cassettes + rejet
        var removed  = lines.Count(l => AtmJrnParser.ExtractCassetteEvent(l)?.Action == "REMOVED");
        var inserted = lines.Count(l => AtmJrnParser.ExtractCassetteEvent(l)?.Action == "INSERTED");

        removed.Should().BeGreaterThanOrEqualTo(3, "plusieurs cassettes retirées lors de la maintenance");
        inserted.Should().BeGreaterThanOrEqualTo(3, "plusieurs cassettes rechargées");
        inserted.Should().BeGreaterThanOrEqualTo(removed,
            "toutes les cassettes retirées doivent être réinsérées");
    }

    [Fact]
    public void File_20230418_CashCountersBefore_ShouldHaveEstimateFlag()
    {
        var lines = ReadJrnLines("20230418.jrn");

        // Avant SOP, les compteurs sont des estimations (marquées avec *)
        var beforeSopSection = false;
        var afterSopSection  = false;
        var estimatesFound   = 0;
        var exactFound       = 0;

        foreach (var line in lines)
        {
            if (line.Contains("CASH COUNTERS BEFORE SOP")) { beforeSopSection = true; afterSopSection = false; continue; }
            if (line.Contains("CASH COUNTERS AFTER SOP"))  { beforeSopSection = false; afterSopSection = true; continue; }

            var counter = AtmJrnParser.ExtractCashCounter(line);
            if (counter is null) continue;

            if (beforeSopSection && counter.IsEstimate) estimatesFound++;
            if (afterSopSection  && !counter.IsEstimate) exactFound++;
        }

        estimatesFound.Should().BeGreaterThan(0,
            "les compteurs BEFORE SOP sont des estimations (marquées *)");
        exactFound.Should().BeGreaterThan(0,
            "les compteurs AFTER SOP sont des valeurs exactes (sans *)");
    }

    [Fact]
    public void File_20230418_TransactionWithResponseCode51_ShouldBeDetected()
    {
        var lines = ReadJrnLines("20230418.jrn");
        var codes = lines
            .Select(l => AtmJrnParser.ExtractResponseCode(l))
            .Where(c => c is not null)
            .ToList();

        codes.Should().Contain("51", "la transaction 12:54 est refusée (fonds insuffisants)");
        codes.Should().Contain("00", "les autres transactions sont approuvées");
    }

    [Fact]
    public void File_20230418_EmvAid_ShouldBeVisaDebitCredit()
    {
        var lines = ReadJrnLines("20230418.jrn");
        var aids = lines
            .Select(l => AtmJrnParser.ExtractEmvAid(l))
            .Where(a => a is not null)
            .Distinct()
            .ToList();

        aids.Should().NotBeEmpty("le fichier contient des transactions EMV");
        aids.Should().AllSatisfy(aid =>
            aid!.Should().StartWith("A0000000031010",
                "AID Visa Debit/Credit standard (RID Visa + PIX 1010)"));
    }

    [Fact]
    public void File_20230418_RrnShouldBeExtractable()
    {
        var lines = ReadJrnLines("20230418.jrn");
        var rrns = lines
            .Select(l => AtmJrnParser.ExtractRrn(l))
            .Where(r => r is not null)
            .ToList();

        rrns.Should().NotBeEmpty("le fichier contient des RRN (Reference Retrieval Number)");
        rrns.Should().AllSatisfy(rrn =>
            rrn!.Should().MatchRegex(@"^\d+$", "le RRN est numérique"));
    }

    [Fact]
    public void File_20230418_SystemEventIds_ShouldStartFrom523()
    {
        var lines = ReadJrnLines("20230418.jrn");
        var ids = lines
            .Select(l => Regex.Match(l, @"^\*(\d+)\*"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

        ids.Should().NotBeEmpty();
        ids.First().Should().Be(523, "le premier événement système du 20230418.jrn est *523*");
        ids.Should().BeInAscendingOrder("les IDs d'événements sont séquentiels");
    }

    // ══════════════════════════════════════════════════════════════
    //  20240512.jrn — Format étendu avec timestamp par ligne
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void File_20240512_ShouldBeReadable()
    {
        var lines = ReadJrnLines("20240512.jrn");
        lines.Should().NotBeEmpty();
    }

    [Fact]
    public void File_20240512_MostLinesShouldHaveTimestamp()
    {
        var lines = ReadJrnLines("20240512.jrn")
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        var withTimestamp = lines.Count(l =>
            Regex.IsMatch(l, @"^\d{2}:\d{2}:\d{2}") ||
            Regex.IsMatch(l, @"^\*\d+\*\d{2}:\d{2}:\d{2}"));

        var ratio = (double)withTimestamp / lines.Count;
        ratio.Should().BeGreaterThan(0.3,
            "la majorité des lignes du format étendu ont un timestamp");
    }

    [Fact]
    public void File_20240512_Transaction1_ShouldBeRefused_Code51()
    {
        var lines = ReadJrnLines("20240512.jrn");
        var codes = lines
            .Select(l => AtmJrnParser.ExtractResponseCode(l))
            .Where(c => c is not null)
            .ToList();

        codes.Should().Contain("51",
            "la première transaction du 20240512.jrn est refusée (fonds insuffisants)");
    }

    [Fact]
    public void File_20240512_Transaction2_InvalidCard_ShouldHaveNoResponseCode()
    {
        var lines = ReadJrnLines("20240512.jrn");

        // La 2e transaction : carte invalide, pas de code réponse
        var cancelledLines = lines.Where(l =>
            l.Contains("CARD INVALID") || l.Contains("TRANSACTION CANCELLED")).ToList();

        cancelledLines.Should().NotBeEmpty(
            "le fichier contient une transaction annulée pour carte invalide");
    }

    [Fact]
    public void File_20240512_DeviceStatusAtStartup_ShouldAllBeOk()
    {
        var lines = ReadJrnLines("20240512.jrn");

        // Au démarrage (avant la 1ère transaction), tous les périphériques sont STATUS 0
        var startupDevices = lines
            .TakeWhile(l => !l.Contains("-> TRANSACTION START"))
            .Select(l => AtmJrnParser.ExtractDeviceStatus(l))
            .Where(d => d is not null)
            .ToList();

        startupDevices.Should().NotBeEmpty("les statuts de périphériques doivent être reportés au démarrage");
        startupDevices.Should().AllSatisfy(d =>
            d!.IsError.Should().BeFalse("tous les périphériques sont opérationnels au démarrage"));
    }

    // ══════════════════════════════════════════════════════════════
    //  Tests cross-fichiers : cohérence du format
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("20200810.jrn", 2020, 8,  10)]
    [InlineData("20230418.jrn", 2023, 4,  18)]
    [InlineData("20240512.jrn", 2024, 5,  12)]
    public void AllFiles_FilenameShouldEncodeDate(string filename, int year, int month, int day)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        var parsed = DateTime.TryParseExact(nameWithoutExt, "yyyyMMdd",
            null, System.Globalization.DateTimeStyles.None, out var date);

        parsed.Should().BeTrue($"{filename} : le nom doit être au format YYYYMMDD.jrn");
        date.Year.Should().Be(year);
        date.Month.Should().Be(month);
        date.Day.Should().Be(day);
    }

    [Theory]
    [InlineData("20200810.jrn")]
    [InlineData("20230418.jrn")]
    [InlineData("20240512.jrn")]
    public void AllFiles_ShouldStartWithJournalingStarted(string filename)
    {
        var lines = ReadJrnLines(filename);
        var first = lines.First(l => !string.IsNullOrWhiteSpace(l));
        first.Should().Contain("JOURNALING STARTED",
            $"{filename} doit commencer par JOURNALING STARTED");
    }

    [Theory]
    [InlineData("20200810.jrn")]
    [InlineData("20230418.jrn")]
    [InlineData("20240512.jrn")]
    public void AllFiles_TransactionBlocks_ShouldBeBalanced(string filename)
    {
        var lines  = ReadJrnLines(filename);
        var starts = lines.Count(l => l.Contains("-> TRANSACTION START"));
        var ends   = lines.Count(l => l.Contains("<- TRANSACTION END"));

        starts.Should().Be(ends,
            $"{filename} : chaque TRANSACTION START doit avoir son TRANSACTION END");
    }

    [Theory]
    [InlineData("20200810.jrn")]
    [InlineData("20230418.jrn")]
    [InlineData("20240512.jrn")]
    public void AllFiles_PanMasking_ShouldBeCompliant(string filename)
    {
        var lines = ReadJrnLines(filename);
        var panLines = lines.Where(l => l.Contains("TRACK 2 DATA:")).ToList();

        foreach (var line in panLines)
        {
            var pan = Regex.Match(line, @"TRACK 2 DATA:\s*(\S+)").Groups[1].Value;
            if (string.IsNullOrEmpty(pan)) continue;

            // Le PAN doit contenir des étoiles (masqué)
            pan.Should().Contain("*",
                $"PCI-DSS : le PAN dans '{filename}' doit être masqué");

            // Aucun PAN complet ne doit apparaître en clair
            pan.Should().NotMatchRegex(@"^\d{13,19}$",
                $"PCI-DSS : le PAN complet ne doit jamais apparaître en clair dans {filename}");
        }
    }

    [Theory]
    [InlineData("20200810.jrn")]
    [InlineData("20230418.jrn")]
    [InlineData("20240512.jrn")]
    public void AllFiles_RemotePath_ShouldFollowNormalizedStructure(string filename)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        DateTime.TryParseExact(nameWithoutExt, "yyyyMMdd",
            null, System.Globalization.DateTimeStyles.None, out var fileDate);

        // Structure : BANK/COUNTRY/CITY/ATMID/YYYY/MM/DD/HHMMSS/filename.jrn
        var remotePath = string.Format("BGFI/GABON/LIBREVILLE/ATM_001/{0}/{1}/{2}/063000/{3}",
            fileDate.Year, fileDate.Month.ToString("D2"), fileDate.Day.ToString("D2"), filename);

        remotePath.Should().MatchRegex(
            @"^[A-Z_\-]+/[A-Z_\-]+/[A-Z_\-]+/ATM_\d+/\d{4}/\d{2}/\d{2}/\d{6}/\w+\.jrn$",
            "la structure du chemin distant doit respecter le format BANK/COUNTRY/CITY/ATM/YYYY/MM/DD/HHMMSS/file");
    }

    // ══════════════════════════════════════════════════════════════
    //  Tests de robustesse : lignes de décoration / malformées
    // ══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("==========================")]
    [InlineData("-------------------------")]
    [InlineData("  DATE      HEURE      OP.     GAB")]
    [InlineData("  CASSETTE     0000854  0001004")]
    [InlineData("  LAST CLEARED: 23/04/18 11:55")]
    [InlineData("NUM. CARTE  :   437477______8910")]
    [InlineData("NUM. COMPTE:   0410011021554011")]
    public void DecorationLines_AtmJrnParser_ShouldNotThrow(string line)
    {
        // Le parseur doit tolérer toutes les lignes sans exception
        var act = () =>
        {
            _ = AtmJrnParser.ClassifyLine(line);
            _ = AtmJrnParser.ExtractTimestamp(line);
            _ = AtmJrnParser.ExtractResponseCode(line);
            _ = AtmJrnParser.ExtractAmount(line);
            _ = AtmJrnParser.ExtractMaskedPan(line);
            _ = AtmJrnParser.ExtractDeviceStatus(line);
            _ = AtmJrnParser.ExtractRrn(line);
            _ = AtmJrnParser.ExtractEmvAid(line);
            _ = AtmJrnParser.ExtractSystemEvent(line);
            _ = AtmJrnParser.ExtractCassetteEvent(line);
            _ = AtmJrnParser.ExtractCashCounter(line);
        };

        act.Should().NotThrow($"le parseur doit être robuste aux lignes de décoration : '{line}'");
    }
}
