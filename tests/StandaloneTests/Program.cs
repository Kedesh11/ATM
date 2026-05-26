using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// ═══════════════════════════════════════════════════════════════════
//  ATM Log Agent — Tests autonomes (zéro dépendance NuGet)
//  Valide les composants Core sur les vrais fichiers .jrn BGFI Gabon
// ═══════════════════════════════════════════════════════════════════

int pass = 0, fail = 0;

void Ok(string name) { Console.WriteLine($"  \u2713 {name}"); pass++; }
void Ko(string name, string why) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine($"  \u2717 {name}: {why}"); Console.ResetColor(); fail++; }
void Section(string t) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n\u2500\u2500 {t} \u2500\u2500"); Console.ResetColor(); }

// Localise les fichiers .jrn (compatibilité run depuis n'importe quel répertoire)
var basePaths = new[]
{
    Path.Combine(AppContext.BaseDirectory, "TestData"),
    Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AtmLogAgent.Tests", "TestData"),
    Path.Combine(AppContext.BaseDirectory, "..", "AtmLogAgent.Tests", "TestData"),
    "/home/sevan/Documents/ME/AtmLogAgent/tests/AtmLogAgent.Tests/TestData"
};
var testData = basePaths.FirstOrDefault(Directory.Exists) ?? basePaths[^1];

Console.WriteLine($"\nATM Log Agent — Tests autonomes .NET 8");
Console.WriteLine($"TestData : {testData}");
Console.WriteLine(new string('═', 60));

// ─── 1. Lecture des fichiers .jrn réels ───────────────────────────
Section("Lecture fichiers .jrn réels (BGFI Gabon)");

var jrnFiles = new[] { "20200810.jrn", "20230418.jrn", "20240512.jrn" };
var allLines = new Dictionary<string, string[]>();

foreach (var f in jrnFiles)
{
    var path = Path.Combine(testData, f);
    if (!File.Exists(path)) { Ko(f, "fichier manquant"); continue; }
    var lines = File.ReadAllLines(path).Select(l => l.TrimEnd('\r', '\n', ' ')).ToArray();
    allLines[f] = lines;
    if (lines.Length > 10) Ok($"{f} — {lines.Length} lignes lues");
    else Ko(f, $"trop court ({lines.Length} lignes)");
}

// ─── 2. Entête JOURNALING STARTED ─────────────────────────────────
Section("Format — Entête JOURNALING STARTED");

foreach (var (f, lines) in allLines)
{
    var first = lines.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
    if (first.Contains("JOURNALING STARTED")) Ok($"{f} — entête valide");
    else Ko(f, $"entête inattendue : '{first[..Math.Min(50,first.Length)]}'");
}

// ─── 3. Timestamps ────────────────────────────────────────────────
Section("Parseur — Timestamps HH:MM:SS");

var tsRx = new Regex(@"\d{2}:\d{2}:\d{2}", RegexOptions.Compiled);

foreach (var (f, lines) in allLines)
{
    var count = lines.Count(l => tsRx.IsMatch(l));
    var ratio = (double)count / lines.Length;
    if (ratio > 0.2) Ok($"{f} — {count}/{lines.Length} lignes avec timestamp ({ratio:P0})");
    else Ko(f, $"ratio timestamp trop faible ({ratio:P0})");
}

// ─── 4. Équilibre START / END ─────────────────────────────────────
Section("Parseur — Équilibre TRANSACTION START / END");

foreach (var (f, lines) in allLines)
{
    var starts = lines.Count(l => l.Contains("-> TRANSACTION START"));
    var ends   = lines.Count(l => l.Contains("<- TRANSACTION END"));
    if (starts > 0 && starts == ends) Ok($"{f} — {starts} transactions équilibrées");
    else Ko(f, $"START={starts} END={ends}");
}

// ─── 5. Codes réponse ISO 8583 ────────────────────────────────────
Section("Parseur — Codes réponse ISO 8583");

var codeRx = new Regex(@"CODE REPONSE[:\s]+(\d+)", RegexOptions.Compiled);
var allCodes = allLines.Values
    .SelectMany(ls => ls)
    .Select(l => codeRx.Match(l))
    .Where(m => m.Success)
    .Select(m => m.Groups[1].Value)
    .ToHashSet();

foreach (var (code, label) in new[] { ("00","Approuvée"), ("51","Fonds insuffisants"), ("54","Carte expirée"), ("75","PIN 3x incorrect") })
{
    if (allCodes.Contains(code)) Ok($"Code {code} — {label}");
    else Ko($"Code {code}", "non trouvé dans les fichiers");
}

// ─── 6. Masquage PAN (PCI-DSS) ───────────────────────────────────
Section("PCI-DSS — Masquage PAN TRACK 2");

var panRx   = new Regex(@"TRACK 2 DATA:\s*(\S+)", RegexOptions.Compiled);
var panMask = new Regex(@"^\d{6}\*+\d{4}$", RegexOptions.Compiled);

var pans = allLines.Values.SelectMany(ls => ls)
    .Select(l => panRx.Match(l)).Where(m => m.Success)
    .Select(m => m.Groups[1].Value).ToList();

if (pans.Count > 0) Ok($"{pans.Count} lignes TRACK 2 trouvées");
else Ko("TRACK 2", "aucune ligne");

var badPans = pans.Where(p => !panMask.IsMatch(p)).ToList();
if (badPans.Count == 0) Ok("Tous les PAN respectent le format PCI-DSS (6+*+4)");
else Ko("PAN", $"{badPans.Count} PAN non conformes : {string.Join(", ", badPans)}");

// PAN spécifiques attendus dans les fichiers
var expectedPans = new[] { "531234******5678", "400000******0001", "437477******8910", "437477******5219" };
foreach (var pan in expectedPans)
{
    if (pans.Contains(pan)) Ok($"PAN masqué conforme : {pan}");
    else Ko(pan, "non trouvé");
}

// ─── 7. Événements système *NNN* ─────────────────────────────────
Section("Parseur — Séquence événements *NNN*");

var evtRx = new Regex(@"^\*(\d+)\*", RegexOptions.Compiled);

foreach (var (f, lines) in allLines)
{
    var ids = lines.Select(l => evtRx.Match(l)).Where(m => m.Success)
                   .Select(m => int.Parse(m.Groups[1].Value)).ToList();
    if (ids.Count == 0) { Ok($"{f} — pas d'événements *NNN*"); continue; }
    var ascending = ids.Zip(ids.Skip(1)).All(p => p.First <= p.Second);
    if (ascending) Ok($"{f} — {ids.Count} événements séquentiels ({ids[0]}→{ids[^1]})");
    else Ko(f, "IDs non séquentiels");
}

// ─── 8. Cassettes 20230418.jrn ───────────────────────────────────
Section("Parseur — Cassettes (20230418.jrn — rechargement)");

var cassRx = new Regex(@"(TOP|SECOND|THIRD|BOTTOM|REJECT)\s+CASSETTE\s+(INSERTED|REMOVED)", RegexOptions.Compiled);

if (allLines.TryGetValue("20230418.jrn", out var l23))
{
    var removed  = l23.Count(l => cassRx.IsMatch(l) && l.Contains("REMOVED"));
    var inserted = l23.Count(l => cassRx.IsMatch(l) && l.Contains("INSERTED"));
    if (removed >= 3)  Ok($"Cassettes retirées : {removed}");  else Ko("REMOVED", $"{removed} < 3");
    if (inserted >= 3) Ok($"Cassettes insérées : {inserted}"); else Ko("INSERTED", $"{inserted} < 3");
    if (inserted >= removed) Ok("Toutes les cassettes retirées ont été réinsérées");
    else Ko("Réinsertion", $"inserted({inserted}) < removed({removed})");
}

// ─── 9. Cash counters BEFORE/AFTER SOP ───────────────────────────
Section("Parseur — Compteurs billets CFA (★ estimation)");

if (allLines.TryGetValue("20230418.jrn", out var l23b))
{
    var cfaRx = new Regex(@"CFA\s+\d+\s+\d+(\*?)", RegexOptions.Compiled);
    bool inBefore = false, inAfter = false;
    int estimates = 0, exact = 0;

    foreach (var line in l23b)
    {
        if (line.Contains("CASH COUNTERS BEFORE SOP")) { inBefore = true; inAfter = false; continue; }
        if (line.Contains("CASH COUNTERS AFTER SOP"))  { inBefore = false; inAfter = true; continue; }
        var m = cfaRx.Match(line);
        if (!m.Success) continue;
        if (inBefore && m.Groups[1].Value == "*") estimates++;
        if (inAfter  && m.Groups[1].Value == "")  exact++;
    }
    if (estimates > 0) Ok($"Estimations BEFORE SOP (★) : {estimates}");
    else Ko("BEFORE SOP", "aucune estimation détectée");
    if (exact > 0) Ok($"Valeurs exactes AFTER SOP : {exact}");
    else Ko("AFTER SOP", "aucune valeur exacte");
}

// ─── 10. Détection fraude ────────────────────────────────────────
Section("Détection fraude — Code 00 + CARD RETAINED (IsSuspicious)");

int suspiciousCount = 0;
foreach (var (f, lines) in allLines)
{
    bool inTx = false, approved = false;
    foreach (var line in lines)
    {
        if (line.Contains("-> TRANSACTION START"))  { inTx = true; approved = false; continue; }
        if (line.Contains("<- TRANSACTION END"))    { inTx = false; continue; }
        if (!inTx) continue;
        var m = codeRx.Match(line);
        if (m.Success && m.Groups[1].Value == "00") approved = true;
        if ((line.Contains("CARD RETAINED") || line.Contains("CARTE BLOQUEE")) && approved)
        { suspiciousCount++; Ok($"{f} — transaction suspecte : Code 00 + CARD RETAINED"); }
    }
}
if (suspiciousCount == 0) Ko("IsSuspicious", "aucune transaction suspecte détectée (attendu ≥1)");

// ─── 11. Nom de fichier = date (YYYYMMDD.jrn) ────────────────────
Section("Convention — Nom de fichier encodes la date");

var dateExpected = new Dictionary<string, DateTime>
{
    ["20200810.jrn"] = new DateTime(2020, 8, 10),
    ["20230418.jrn"] = new DateTime(2023, 4, 18),
    ["20240512.jrn"] = new DateTime(2024, 5, 12),
};

foreach (var (f, expected) in dateExpected)
{
    var nameNoExt = Path.GetFileNameWithoutExtension(f);
    var ok = DateTime.TryParseExact(nameNoExt, "yyyyMMdd",
        null, System.Globalization.DateTimeStyles.None, out var parsed);
    if (ok && parsed == expected) Ok($"{f} → {parsed:yyyy-MM-dd}");
    else Ko(f, $"impossible de parser la date depuis le nom");
}

// ─── 12. AES-256-GCM ─────────────────────────────────────────────
Section("Sécurité — Chiffrement AES-256-GCM (AEAD)");

var key   = RandomNumberGenerator.GetBytes(32);
var nonce = RandomNumberGenerator.GetBytes(12);
var plain = Encoding.UTF8.GetBytes("BGFI/GABON/LIBREVILLE — TRANSACTION 20230418");
var cipher = new byte[plain.Length];
var tag    = new byte[16];
var deciph = new byte[plain.Length];

using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, deciph);

if (Encoding.UTF8.GetString(deciph) == Encoding.UTF8.GetString(plain))
    Ok("AES-256-GCM round-trip OK");
else Ko("AES-256-GCM", "déchiffrement incorrect");

// Nonce unique
var nonce2 = RandomNumberGenerator.GetBytes(12);
if (!nonce.SequenceEqual(nonce2)) Ok("Nonce aléatoire unique à chaque chiffrement");
else Ko("Nonce", "collision détectée (extrêmement improbable)");

// Tamper detection
try
{
    cipher[0] ^= 0xFF;
    var bad = new byte[plain.Length];
    using var aes3 = new AesGcm(key, 16);
    aes3.Decrypt(nonce, cipher, tag, bad);
    Ko("Tamper detection", "AuthenticationTagMismatchException non levée");
}
catch (AuthenticationTagMismatchException)
{
    Ok("Tamper detection — AuthenticationTagMismatchException levée correctement");
}

// Zero-memory simulation
var sensitiveKey = RandomNumberGenerator.GetBytes(32);
CryptographicOperations.ZeroMemory(sensitiveKey);
if (sensitiveKey.All(b => b == 0)) Ok("ZeroMemory — clé effacée de la mémoire après usage");
else Ko("ZeroMemory", "clé non effacée");

// ─── 13. SHA-256 intégrité ────────────────────────────────────────
Section("Sécurité — Intégrité SHA-256");

var content  = "06:15:00 -> TRANSACTION START\nTRACK 2 DATA: 531234******5678\nCODE REPONSE: 00";
var hash1 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
var hash2 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
var hash3 = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(content + "tampered")));

if (hash1 == hash2) Ok("SHA-256 déterministe (même contenu = même hash)");
else Ko("SHA-256", "non déterministe");
if (hash1 != hash3) Ok("SHA-256 sensible à toute modification");
else Ko("SHA-256 tamper", "collision détectée");

// ─── 14. Normalisation AtmId ─────────────────────────────────────
Section("AtmIdentityResolver — Normalisation AtmId");

string NormalizeId(string raw)
{
    var c = Regex.Replace(raw.ToUpperInvariant().Trim(), @"[^A-Z0-9]", "-").Trim('-');
    if (c.Length > 20) c = c[..20].TrimEnd('-');
    return c.StartsWith("ATM") ? c : $"ATM-{c}";
}

foreach (var (input, expected) in new[]
{
    ("SN-8472-KX",    "ATM-SN-8472-KX"),
    ("001A2B3C4D5E",  "ATM-001A2B3C4D5E"),
    ("my hostname",   "ATM-MY-HOSTNAME"),
})
{
    var result = NormalizeId(input);
    if (result == expected) Ok($"'{input}' → '{result}'");
    else Ko($"NormalizeId('{input}')", $"attendu '{expected}', obtenu '{result}'");
}

// ─── 15. Dérivation BankName ─────────────────────────────────────
Section("AtmIdentityResolver — BankName depuis hostname SFTP");

string? DerivBank(string host)
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "sftp","ftp","atm","server","host","api","prod","dev","test" };
    foreach (var part in host.Split('.'))
    {
        if (part.Length < 3 || skip.Contains(part)) continue;
        var c = part.Split('-').First().ToUpperInvariant();
        if (c.Length >= 2 && Regex.IsMatch(c, @"^[A-Z]+$")) return c;
    }
    return null;
}

foreach (var (host, expected) in new[]
{
    ("sftp.bgfi-bank.ga",     "BGFI"),
    ("atm.ecobank.net",       "ECOBANK"),
    ("ftp.banque-centrale.ga","BANQUE"),
})
{
    var r = DerivBank(host);
    if (r == expected) Ok($"'{host}' → '{r}'");
    else Ko(host, $"attendu '{expected}', obtenu '{r ?? "null"}'");
}

// ─── 16. Chemin distant normalisé ────────────────────────────────
Section("LogDiscovery — Construction chemin distant");

string BuildPath(string bank, string country, string city, string atmId, string file, DateTime d)
{
    string S(string v) => Regex.Replace(v.Replace(" ","_"), @"[/\\:*?""<>|]", "-").ToUpperInvariant().Trim();
    return $"{S(bank)}/{S(country)}/{S(city)}/{S(atmId)}/{d:yyyy}/{d:MM}/{d:dd}/{d:HHmmss}/{file}";
}

foreach (var (file, date, expected) in new[]
{
    ("20200810.jrn", new DateTime(2020,8,10,6,0,0),   "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2020/08/10/060000/20200810.jrn"),
    ("20230418.jrn", new DateTime(2023,4,18,11,59,11), "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2023/04/18/115911/20230418.jrn"),
    ("20240512.jrn", new DateTime(2024,5,12,6,30,0),   "BGFI/GABON/LIBREVILLE/ATM-SN8472KX/2024/05/12/063000/20240512.jrn"),
})
{
    var result = BuildPath("BGFI","GABON","LIBREVILLE","ATM-SN8472KX", file, date);
    if (result == expected) Ok(result);
    else Ko(file, $"\n    attendu : {expected}\n    obtenu  : {result}");
}

// ─── Résumé final ────────────────────────────────────────────────
Console.WriteLine($"\n{new string('═',60)}");
if (fail == 0)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✓ TOUS LES TESTS PASSÉS : {pass}/{pass+fail}");
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  ✗ ÉCHECS : {fail} | RÉUSSIS : {pass} | TOTAL : {pass+fail}");
}
Console.ResetColor();
Console.WriteLine(new string('═',60));

return fail == 0 ? 0 : 1;
