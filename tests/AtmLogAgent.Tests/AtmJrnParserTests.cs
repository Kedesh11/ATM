using AtmLogAgent.Core.Parsers;
using FluentAssertions;
using Xunit;

namespace AtmLogAgent.Tests;

/// <summary>
/// Tests unitaires du parseur AtmJrnParser basés sur les lignes réelles des fichiers .jrn.
/// </summary>
public sealed class AtmJrnParserTests
{
    // ── Timestamp extraction ─────────────────────────────────

    [Theory]
    [InlineData("06:00:00 JOURNALING STARTED",           6, 0, 0)]
    [InlineData("06:15:00 -> TRANSACTION START",         6, 15, 0)]
    [InlineData("*1000*06:00:02 OPERATOR DOOR OPENED",   6, 0, 2)]
    [InlineData("*523*11:43:25 OPERATOR DOOR OPENED",    11, 43, 25)]
    [InlineData("20:28:45 -> TRANSACTION START",         20, 28, 45)]
    public void ExtractTimestamp_ShouldParseCorrectly(string line, int h, int m, int s)
    {
        var ts = AtmJrnParser.ExtractTimestamp(line);
        ts.Should().NotBeNull();
        ts!.Value.Hours.Should().Be(h);
        ts.Value.Minutes.Should().Be(m);
        ts.Value.Seconds.Should().Be(s);
    }

    [Theory]
    [InlineData("CODE REPONSE: 00")]
    [InlineData("PIN ENTERED")]
    [InlineData("CASH TAKEN")]
    [InlineData("CARD TAKEN")]
    public void ExtractTimestamp_OnLinesWithoutTimestamp_ShouldReturnNull(string line)
    {
        AtmJrnParser.ExtractTimestamp(line).Should().BeNull();
    }

    // ── Response code extraction ─────────────────────────────

    [Theory]
    [InlineData("CODE REPONSE: 00", "00")]
    [InlineData("CODE REPONSE: 51", "51")]
    [InlineData("CODE REPONSE: 54", "54")]
    [InlineData("CODE REPONSE: 75", "75")]
    [InlineData("CODE REPONSE:  00", "00")]      // Espaces variables
    public void ExtractResponseCode_ShouldParseAllFormats(string line, string expected)
    {
        AtmJrnParser.ExtractResponseCode(line).Should().Be(expected);
    }

    [Fact]
    public void IsApproved_WithCode00_ShouldBeTrue()
    {
        AtmJrnParser.IsApproved("00").Should().BeTrue();
        AtmJrnParser.IsApproved("51").Should().BeFalse();
        AtmJrnParser.IsApproved("54").Should().BeFalse();
        AtmJrnParser.IsApproved("75").Should().BeFalse();
    }

    // ── Amount extraction ────────────────────────────────────

    [Theory]
    [InlineData("AMOUNT 30000 ENTERED",     30000)]
    [InlineData("AMOUNT 50000 ENTERED",     50000)]
    [InlineData("AMOUNT 20000 ENTERED",     20000)]
    [InlineData("AMOUNT 70000 ENTERED",     70000)]
    [InlineData("AMOUNT 25000 ENTERED",     25000)]
    [InlineData("MONTANT:  20000   XAF",    20000)]
    [InlineData("MONTANT:  30000   XAF",    30000)]
    [InlineData("MONTANT:  50000   XAF",    50000)]
    [InlineData("MONTANT:  70000   XAF",    70000)]
    public void ExtractAmount_ShouldParseBothFormats(string line, long expected)
    {
        AtmJrnParser.ExtractAmount(line).Should().Be(expected);
    }

    // ── Device status extraction ─────────────────────────────

    [Theory]
    [InlineData("06:00:04 DEVICE CCCardFW STATUS 0 SUPPLY 1",   "CCCardFW",   0, 1)]
    [InlineData("06:00:04 DEVICE CCCdmFW STATUS 0 SUPPLY 1",    "CCCdmFW",    0, 1)]
    [InlineData("11:43:27 DEVICE CCRecPrtFW STATUS 4 SUPPLY 1", "CCRecPrtFW", 4, 1)]
    [InlineData("11:43:27 DEVICE CCCamFW STATUS 0 SUPPLY 0",    "CCCamFW",    0, 0)]
    [InlineData("11:43:27 DEVICE LOG_CASS_1 STATUS 0 SUPPLY 0", "LOG_CASS_1", 0, 0)]
    [InlineData("11:43:27 DEVICE LOG_CASS_3 STATUS 0 SUPPLY 1", "LOG_CASS_3", 0, 1)]
    public void ExtractDeviceStatus_ShouldParseAllDevices(
        string line, string deviceName, int status, int supply)
    {
        var info = AtmJrnParser.ExtractDeviceStatus(line);
        info.Should().NotBeNull();
        info!.DeviceName.Should().Be(deviceName);
        info.Status.Should().Be(status);
        info.Supply.Should().Be(supply);
    }

    [Fact]
    public void DeviceStatus_IsError_WhenStatusNonZero()
    {
        var errorDevice = AtmJrnParser.ExtractDeviceStatus(
            "DEVICE CCRecPrtFW STATUS 4 SUPPLY 1");
        errorDevice!.IsError.Should().BeTrue("STATUS 4 = erreur");

        var okDevice = AtmJrnParser.ExtractDeviceStatus(
            "DEVICE CCCardFW STATUS 0 SUPPLY 1");
        okDevice!.IsError.Should().BeFalse("STATUS 0 = OK");
    }

    // ── System event extraction ──────────────────────────────

    [Theory]
    [InlineData("*1000*06:00:02 OPERATOR DOOR OPENED",  1000, "OPERATOR DOOR OPENED")]
    [InlineData("*1001*06:00:03 COMMUNICATION ONLINE",  1001, "COMMUNICATION ONLINE")]
    [InlineData("*1002*06:00:05 GO IN SERVICE COMMAND", 1002, "GO IN SERVICE COMMAND")]
    [InlineData("*523*11:43:25 OPERATOR DOOR OPENED",   523,  "OPERATOR DOOR OPENED")]
    [InlineData("*900*06:30:10 OPERATOR DOOR OPENED",   900,  "OPERATOR DOOR OPENED")]
    public void ExtractSystemEvent_ShouldParseEventIds(string line, int id, string desc)
    {
        var evt = AtmJrnParser.ExtractSystemEvent(line);
        evt.Should().NotBeNull();
        evt!.EventId.Should().Be(id);
        evt.Description.Should().Be(desc);
    }

    // ── Cassette events ──────────────────────────────────────

    [Theory]
    [InlineData("*527*11:43:40 THIRD CASSETTE REMOVED",   "THIRD",  "REMOVED")]
    [InlineData("*531*11:45:48 BOTTOM CASSETTE REMOVED",  "BOTTOM", "REMOVED")]
    [InlineData("*537*11:50:54 TOP CASSETTE INSERTED",    "TOP",    "INSERTED")]
    [InlineData("*540*11:52:10 SECOND CASSETTE INSERTED", "SECOND", "INSERTED")]
    [InlineData("*543*11:52:57 THIRD CASSETTE INSERTED",  "THIRD",  "INSERTED")]
    [InlineData("*546*11:53:39 BOTTOM CASSETTE INSERTED", "BOTTOM", "INSERTED")]
    [InlineData("*549*11:53:51 REJECT CASSETTE INSERTED", "REJECT", "INSERTED")]
    public void ExtractCassetteEvent_ShouldParseAllTypes(string line, string cassette, string action)
    {
        var evt = AtmJrnParser.ExtractCassetteEvent(line);
        evt.Should().NotBeNull();
        evt!.CassetteId.Should().Be(cassette);
        evt.Action.Should().Be(action);
        evt.IsInsertion.Should().Be(action == "INSERTED");
        evt.IsRemoval.Should().Be(action == "REMOVED");
    }

    // ── Cash counter parsing ─────────────────────────────────

    [Theory]
    [InlineData("   CFA    10000 1553*", 10000, 1553, true)]   // Avant SOP (estimation)
    [InlineData("   CFA     5000  290*", 5000,   290, true)]
    [InlineData("   CFA    10000  853",  10000,  853, false)]   // Après SOP (réel)
    [InlineData("   CFA     5000 1003",  5000,  1003, false)]
    [InlineData("   CFA    10000  599",  10000,  599, false)]
    [InlineData("   CFA     5000 1011",  5000,  1011, false)]
    public void ExtractCashCounter_ShouldParseDenominationsAndCounts(
        string line, int denomination, int count, bool isEstimate)
    {
        var counter = AtmJrnParser.ExtractCashCounter(line);
        counter.Should().NotBeNull();
        counter!.Denomination.Should().Be(denomination);
        counter.Count.Should().Be(count);
        counter.IsEstimate.Should().Be(isEstimate);
        counter.TotalValue.Should().Be((long)denomination * count);
    }

    // ── Line classification ──────────────────────────────────

    [Theory]
    [InlineData("06:15:00 -> TRANSACTION START",           JrnLineType.TransactionStart)]
    [InlineData("<- TRANSACTION END",                       JrnLineType.TransactionEnd)]
    [InlineData("*1000*06:00:02 OPERATOR DOOR OPENED",     JrnLineType.SystemEvent)]
    [InlineData("DEVICE CCCardFW STATUS 0 SUPPLY 1",        JrnLineType.DeviceStatus)]
    [InlineData("TRACK 2 DATA: 531234******5678",            JrnLineType.TrackData)]
    [InlineData("CODE REPONSE: 00",                         JrnLineType.ResponseCode)]
    [InlineData("AMOUNT 30000 ENTERED",                     JrnLineType.Amount)]
    [InlineData("TOP CASSETTE INSERTED",                    JrnLineType.CassetteEvent)]
    [InlineData("COMMUNICATION ONLINE",                     JrnLineType.CommunicationEvent)]
    [InlineData("   CFA    10000 1553*",                    JrnLineType.CashCounter)]
    [InlineData("PIN ENTERED",                              JrnLineType.PinEvent)]
    [InlineData("CARD RETAINED",                            JrnLineType.CardRetained)]
    [InlineData("",                                         JrnLineType.Empty)]
    [InlineData("==========================",               JrnLineType.Other)]
    public void ClassifyLine_ShouldReturnCorrectType(string line, JrnLineType expected)
    {
        AtmJrnParser.ClassifyLine(line).Should().Be(expected);
    }

    // ── Transaction block parsing ────────────────────────────

    [Fact]
    public void ParseTransactionBlock_ApprovedWithdrawal_ShouldExtractAllFields()
    {
        var block = new[]
        {
            "06:15:00 -> TRANSACTION START",
            "TRACK 2 DATA: 531234******5678",
            "EMV AID A0000000031010 STARTED",
            "PIN ENTERED",
            "AMOUNT 30000 ENTERED",
            "TRANSACTION REQUEST ABAI",
            "CODE REPONSE: 00",
            "RRN: 310865672052",
            "CASH PRESENTED",
            "CASH TAKEN",
            "<- TRANSACTION END"
        };

        var summary = AtmJrnParser.ParseTransactionBlock(block);

        summary.Should().NotBeNull();
        summary!.MaskedPan.Should().Be("531234******5678");
        summary.ResponseCode.Should().Be("00");
        summary.Amount.Should().Be(30000);
        summary.Rrn.Should().Be("310865672052");
        summary.CashTaken.Should().BeTrue();
        summary.IsApproved.Should().BeTrue();
        summary.CardRetained.Should().BeFalse();
    }

    [Fact]
    public void ParseTransactionBlock_InsufficientFunds_ShouldDetectDecline()
    {
        var block = new[]
        {
            "06:45:00 -> TRANSACTION START",
            "TRACK 2 DATA: 400000******0001",
            "AMOUNT 50000 ENTERED",
            "CODE REPONSE: 51",
            "MESSAGE: FONDS INSUFFISANTS",
            "<- TRANSACTION END"
        };

        var summary = AtmJrnParser.ParseTransactionBlock(block);

        summary!.ResponseCode.Should().Be("51");
        summary.Amount.Should().Be(50000);
        summary.IsApproved.Should().BeFalse();
        summary.CashTaken.Should().BeFalse();
    }

    [Fact]
    public void ParseTransactionBlock_CardRetainedAfterApproval_ShouldFlagSuspicious()
    {
        // Scénario : transaction approuvée mais carte retenue (anormal)
        var block = new[]
        {
            "09:45:00 -> TRANSACTION START",
            "TRACK 2 DATA: 400000******0001",
            "AMOUNT 20000 ENTERED",
            "CODE REPONSE: 00",
            "CARD NOT TAKEN",
            "CARD RETAINED",       // La carte n'a pas été reprise → retenue
            "<- TRANSACTION END"
        };

        var summary = AtmJrnParser.ParseTransactionBlock(block);

        summary!.IsApproved.Should().BeTrue();
        summary.CardRetained.Should().BeTrue();
        summary.IsSuspicious.Should().BeTrue("carte retenue après transaction approuvée = suspect");
    }
}
