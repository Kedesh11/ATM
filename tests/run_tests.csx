#!/usr/bin/env dotnet-script
// Tests autonomes — zéro dépendance NuGet
// Lit les vrais fichiers .jrn et valide les composants Core

using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

var testData = Path.Combine(AppContext.BaseDirectory,
    "tests/AtmLogAgent.Tests/TestData");

var pass = 0; var fail = 0;

void Ok(string name) { Console.WriteLine($"  ✓ {name}"); pass++; }
void Ko(string name, string why) { Console.WriteLine($"  ✗ {name}: {why}"); fail++; }
void Section(string t) => Console.WriteLine($"\n── {t} ──");

// ── Lecture fichiers .jrn ──────────────────────────────────
Section("Lecture fichiers .jrn réels");

var files = new[] { "20200810.jrn", "20230418.jrn", "20240512.jrn" };
var allLines = new Dictionary<string, string[]>();

foreach (var f in files)
{
    var path = Path.Combine(testData, f);
    if (!File.Exists(path)) { Ko(f, "fichier manquant"); continue; }
    var lines = File.ReadAllLines(path).Select(l => l.TrimEnd('\r', '\n', ' ')).ToArray();
    allLines[f] = lines;
    if (lines.Length > 10) Ok($"{f} lu ({lines.Length} lignes)");
    else Ko(f, $"trop court ({lines.Length} lignes)");
}

// ── Parseur Timestamps ─────────────────────────────────────
Section("Parseur — Timestamps");

var tsRx = new Regex(@"(\d{2}):(\d{2}):(\d{2})", RegexOptions.Compiled);

foreach (var (f, lines) in allLines)
{
    var withTs = lines.Count(l => tsRx.IsMatch(l));
    var ratio = (double)withTs / lines.Length;
    if (ratio > 0.2) Ok($"{f} — {withTs}/{lines.Length} lignes avec timestamp ({ratio:P0})");
    else Ko(f, $"ratio timestamp trop faible ({ratio:P0})");
}

// ── Transactions équilibrées ───────────────────────────────
Section("Parseur — Équilibre START/END");

foreach (var (f, lines) in allLines)
{
    var starts = lines.Count(l => l.Contains("-> TRANSACTION START"));
    var ends   = lines.Count(l => l.Contains("<- TRANSACTION END"));
    if (starts > 0 && starts == ends) Ok($"{f} — {starts} transactions équilibrées");
    else Ko(f, $"START={starts} END={ends}");
}

// ── Codes réponse ISO 8583 ─────────────────────────────────
Section("Parseur — Codes réponse (ISO 8583)");

var codeRx = new Regex(@"CODE REPONSE[:\s]+(\d+)", RegexOptions.Compiled);

var allCodes = allLines.Values
    .SelectMany(ls => ls)
    .Select(l => codeRx.Match(l))
    .Where(m => m.Success)
    .Select(m => m.Groups[1].Value)
    .ToHashSet();

foreach (var expected in new[] { "00", "51", "54", "75" })
{
    if (allCodes.Contains(expected)) Ok($"Code {expected} détecté");
    else Ko($"Code {expected}", "non trouvé");
}

// ── Masquage PAN (PCI-DSS) ────────────────────────────────
Section("PCI-DSS — Masquage PAN");

var panRx   = new Regex(@"TRACK 2 DATA:\s*(\S+)", RegexOptions.Compiled);
var panMask = new Regex(@"^\d{6}\*+\d{4}$", RegexOptions.Compiled);
var panFull = new Regex(@"^\d{13,19}$", RegexOptions.Compiled);

var pans = allLines.Values
    .SelectMany(ls => ls)
    .Select(l => panRx.Match(l))
    .Where(m => m.Success)
    .Select(m => m.Groups[1].Value)
    .ToList();

if (pans.Count > 0) Ok($"{pans.Count} lignes TRACK 2 trouvées");
else Ko("TRACK 2", "aucune ligne trouvée");

var badPans = pans.Where(p => !panMask.IsMatch(p) || panFull.IsMatch(p)).ToList();
if (badPans.Count == 0) Ok("Tous les PAN sont masqués correctement");
else Ko("PAN non masqués", string.Join(", ", badPans));

// ── Événements système *NNN* ───────────────────────────────
Section("Parseur — Événements système");

var evtRx = new Regex(@"^\*(\d+)\*", RegexOptions.Compiled);

foreach (var (f, lines) in allLines)
{
    var ids = lines
        .Select(l => evtRx.Match(l))
        .Where(m => m.Success)
        .Select(m => int.Parse(m.Groups[1].Value))
        .ToList();

    if (ids.Count == 0) { Ok($"{f} — pas d'événements *NNN* (normal)"); continue; }

    var sorted = ids.Zip(ids.Skip(1)).All(p => p.First <= p.Second);
    if (sorted) Ok($"{f} — {ids.Count} événements système séquentiels ({ids[0]}→{ids[^1]})");
    else Ko(f, "IDs non séquentiels");
}

// ── Cassettes (20230418.jrn) ──────────────────────────────
Section("Parseur — Événements cassette (20230418.jrn)");

var cassRx = new Regex(@"(TOP|SECOND|THIRD|BOTTOM|REJECT)\s+CASSETTE\s+(INSERTED|REMOVED)", RegexOptions.Compiled);

if (allLines.TryGetValue("20230418.jrn", out var lines23))
{
    var removed  = lines23.Count(l => cassRx.IsMatch(l) && l.Contains("REMOVED"));
    var inserted = lines23.Count(l => cassRx.IsMatch(l) && l.Contains("INSERTED"));
    if (removed >= 3) Ok($"Cassettes retirées : {removed}");
    else Ko("REMOVED", $"seulement {removed}");
    if (inserted >= 3) Ok($"Cassettes insérées : {inserted}");
    else Ko("INSERTED", $"seulement {inserted}");
}

// ── Cash Counters (étoile = estimation) ───────────────────
Section("Parseur — Compteurs billets (BEFORE/AFTER SOP)");

if (allLines.TryGetValue("20230418.jrn", out var lines23b))
{
    var cfaRx = new Regex(@"CFA\s+\d+\s+(\d+)(\*?)", RegexOptions.Compiled);
    bool inBefore = false, inAfter = false;
    int estimates = 0, exact = 0;

    foreach (var line in lines23b)
    {
        if (line.Contains("CASH COUNTERS BEFORE SOP")) { inBefore = true; inAfter = false; continue; }
        if (line.Contains("CASH COUNTERS AFTER SOP"))  { inBefore = false; inAfter = true; continue; }
        var m = cfaRx.Match(line);
        if (!m.Success) continue;
        if (inBefore && m.Groups[2].Value == "*") estimates++;
        if (inAfter  && m.Groups[2].Value == "")  exact++;
    }

    if (estimates > 0) Ok($"Estimations BEFORE SOP (★) : {estimates}");
    else Ko("BEFORE SOP", "aucune estimation trouvée");
    if (exact > 0) Ok($"Valeurs exactes AFTER SOP : {exact}");
    else Ko("AFTER SOP", "aucune valeur exacte");
}

// ── Transaction suspecte (code 00 + CARD RETAINED) ────────
Section("Détection fraude — CardRetained après approbation");

foreach (var (f, lines) in allLines)
{
    bool inTx = false, approved = false, suspicious = false;
    foreach (var line in lines)
    {
        if (line.Contains("-> TRANSACTION START"))  { inTx = true; approved = false; continue; }
        if (line.Contains("<- TRANSACTION END"))    { inTx = false; continue; }
        if (!inTx) continue;
        if (codeRx.Match(line) is { Success: true } m && m.Groups[1].Value == "00") approved = true;
        if ((line.Contains("CARD RETAINED") || line.Contains("CARTE BLOQUEE")) && approved)
            suspicious = true;
    }
    if (suspicious) Ok($"{f} — transaction suspecte détectée (00 + CARD RETAINED)");
}

// ── Chiffrement AES-256-GCM ────────────────────────────────
Section("Chiffrement AES-256-GCM (sans dépendances)");

var key = RandomNumberGenerator.GetBytes(32);
var nonce = RandomNumberGenerator.GetBytes(12);
var plaintext = Encoding.UTF8.GetBytes("TEST LOG ENTRY — BGFI GABON 20230418");
var ciphertext = new byte[plaintext.Length];
var tag = new byte[16];

using (var aes = new AesGcm(key, 16))
{
    aes.Encrypt(nonce, plaintext, ciphertext, tag);
}

var decrypted = new byte[plaintext.Length];
using (var aes = new AesGcm(key, 16))
{
    aes.Decrypt(nonce, ciphertext, tag, decrypted);
}

if (Encoding.UTF8.GetString(decrypted) == Encoding.UTF8.GetString(plaintext))
    Ok("AES-256-GCM round-trip OK");
else Ko("AES-256-GCM", "déchiffrement incorrect");

// Tamper detection
try
{
    ciphertext[0] ^= 0xFF;
    var tampered = new byte[plaintext.Length];
    using var aes2 = new AesGcm(key, 16);
    aes2.Decrypt(nonce, ciphertext, tag, tampered);
    Ko("Tamper detection", "devrait lever AuthenticationTagMismatchException");
}
catch (AuthenticationTagMismatchException)
{
    Ok("Tamper detection : AuthenticationTagMismatchException levée correctement");
}

// ── SHA-256 intégrité ──────────────────────────────────────
Section("Intégrité SHA-256");

var content = "06:15:00 -> TRANSACTION START\nTRACK 2 DATA: 531234******5678";
var hash1 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
var hash2 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
var hash3 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content + "x")));

if (hash1 == hash2) Ok("SHA-256 déterministe (même entrée = même hash)");
else Ko("SHA-256", "non déterministe");
if (hash1 != hash3) Ok("SHA-256 sensible aux modifications");
else Ko("SHA-256", "collision détectée");

// ── Normalisation AtmId ────────────────────────────────────
Section("AtmIdentityResolver — Normalisation AtmId");

string NormalizeAtmId(string raw)
{
    var cleaned = Regex.Replace(raw.ToUpperInvariant().Trim(), @"[^A-Z0-9]", "-").Trim('-');
    if (cleaned.Length > 20) cleaned = cleaned[..20].TrimEnd('-');
    return cleaned.StartsWith("ATM") ? cleaned : $"ATM-{cleaned}";
}

var cases = new[] {
    ("SN-8472-KX",     "ATM-SN-8472-KX"),
    ("001A2B3C4D5E",   "ATM-001A2B3C4D5E"),
    ("ATM_GABON_001",  "ATM-GABON-001"),
    ("my hostname",    "ATM-MY-HOSTNAME"),
};

foreach (var (input, expected) in cases)
{
    var result = NormalizeAtmId(input);
    if (result == expected) Ok($"'{input}' → '{result}'");
    else Ko($"NormalizeAtmId('{input}')", $"attendu '{expected}', obtenu '{result}'");
}

// ── Dérivation BankName depuis SFTP ───────────────────────
Section("AtmIdentityResolver — BankName depuis hostname SFTP");

string? DeriveBankFromSftp(string host)
{
    var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "sftp", "ftp", "atm", "server", "host", "api", "prod", "dev" };
    foreach (var part in host.Split('.'))
    {
        if (part.Length < 3 || generic.Contains(part)) continue;
        var c = part.Split('-').First().ToUpperInvariant();
        if (c.Length >= 2 && Regex.IsMatch(c, @"^[A-Z]+$")) return c;
    }
    return null;
}

var sftpCases = new[] {
    ("sftp.bgfi-bank.ga",    "BGFI"),
    ("atm.ecobank.net",      "ECOBANK"),
    ("ftp.banque-centrale.ga","BANQUE"),
};

foreach (var (host, expected) in sftpCases)
{
    var result = DeriveBankFromSftp(host);
    if (result == expected) Ok($"'{host}' → '{result}'");
    else Ko(host, $"attendu '{expected}', obtenu '{result ?? "null"}'");
}

// ── Chemin distant normalisé ───────────────────────────────
Section("Structure chemin distant");

string BuildRemotePath(string bank, string country, string city, string atmId, string file, DateTime date)
{
    string Sanitize(string v) => Regex.Replace(v.Replace(" ","_"), @"[/\\:*?""<>|]","-").ToUpperInvariant().Trim();
    return $"{Sanitize(bank)}/{Sanitize(country)}/{Sanitize(city)}/{Sanitize(atmId)}" +
           $"/{date:yyyy}/{date:MM}/{date:dd}/{date:HHmmss}/{file}";
}

var pathCases = new[]
{
    ("20200810.jrn", new DateTime(2020,8,10,6,0,0),  "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2020/08/10/060000/20200810.jrn"),
    ("20230418.jrn", new DateTime(2023,4,18,11,59,0), "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2023/04/18/115900/20230418.jrn"),
    ("20240512.jrn", new DateTime(2024,5,12,6,30,0),  "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2024/05/12/063000/20240512.jrn"),
};

foreach (var (file, date, expected) in pathCases)
{
    var result = BuildRemotePath("BGFI","GABON","LIBREVILLE","ATM-SN8472KX", file, date);
    if (result == expected) Ok($"{file} → {result}");
    else Ko(file, $"\n    attendu  : {expected}\n    obtenu   : {result}");
}

// ── Résumé ─────────────────────────────────────────────────
Console.WriteLine($"\n{'─',60}");
Console.WriteLine($"  RÉSULTATS : {pass} réussis  |  {fail} échoués  |  {pass+fail} total");
Console.WriteLine($"{'─',60}");
return fail == 0 ? 0 : 1;
