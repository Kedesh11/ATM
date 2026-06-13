using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace AtmLogAgent.SetupWizard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new SetupWizardForm());
    }
}

internal sealed class SetupWizardForm : Form
{
    private readonly TextBox _installPath = new();
    private readonly TextBox _dataPath = new();
    private readonly TextBox _serviceName = new();
    private readonly TextBox _bankName = new();
    private readonly TextBox _country = new();
    private readonly TextBox _city = new();
    private readonly TextBox _atmId = new();
    private readonly TextBox _manufacturer = new();
    private readonly TextBox _model = new();
    private readonly TextBox _sftpHost = new();
    private readonly NumericUpDown _sftpPort = new();
    private readonly TextBox _sftpUser = new();
    private readonly TextBox _sftpFingerprint = new();
    private readonly TextBox _heartbeatUrl = new();
    private readonly TextBox _updateServerUrl = new();
    private readonly TextBox _watchPaths = new();
    private readonly CheckBox _generateSshKey = new();
    private readonly CheckBox _installService = new();
    private readonly CheckBox _startService = new();
    private readonly TextBox _output = new();

    public SetupWizardForm()
    {
        Text = "ATM Log Agent - Assistant d'installation";
        Width = 980;
        Height = 820;
        MinimumSize = new Size(900, 700);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildFormBody(), 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);

        ApplyDefaults();
    }

    private Control BuildHeader()
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var title = new Label
        {
            Text = "Configuration et installation Windows",
            Font = new Font(Font.FontFamily, 16, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 4)
        };
        var subtitle = new Label
        {
            Text = "Saisissez les informations ATM, SFTP et supervision. L'assistant genere appsettings.json, les cles et le service Windows.",
            AutoSize = true,
            Location = new Point(2, 40)
        };

        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control BuildFormBody()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildInstallationTab());
        tabs.TabPages.Add(BuildAtmTab());
        tabs.TabPages.Add(BuildSftpTab());
        tabs.TabPages.Add(BuildAdvancedTab());
        return tabs;
    }

    private TabPage BuildInstallationTab()
    {
        var page = new TabPage("Installation");
        var table = CreateTable();

        AddPathRow(table, "Repertoire d'installation", _installPath);
        _dataPath.ReadOnly = true;
        _dataPath.BackColor = SystemColors.Control;
        AddTextRow(table, "Repertoire de donnees auto", _dataPath);
        AddTextRow(table, "Nom du service Windows", _serviceName);

        _generateSshKey.Text = "Generer une cle SSH ED25519 si elle n'existe pas";
        _generateSshKey.Checked = true;
        AddControlRow(table, "", _generateSshKey);

        _installService.Text = "Installer ou mettre a jour le service Windows";
        _installService.Checked = true;
        AddControlRow(table, "", _installService);

        _startService.Text = "Demarrer le service apres installation";
        _startService.Checked = true;
        AddControlRow(table, "", _startService);

        page.Controls.Add(WrapScrollable(table));
        return page;
    }

    private TabPage BuildAtmTab()
    {
        var page = new TabPage("Identite ATM");
        var table = CreateTable();

        AddTextRow(table, "Banque", _bankName);
        AddTextRow(table, "Pays", _country);
        AddTextRow(table, "Ville", _city);
        _atmId.ReadOnly = true;
        _atmId.BackColor = SystemColors.Control;
        AddTextRow(table, "Identifiant ATM automatique", _atmId);
        AddTextRow(table, "Fabricant", _manufacturer);
        AddTextRow(table, "Modele", _model);

        page.Controls.Add(WrapScrollable(table));
        return page;
    }

    private TabPage BuildSftpTab()
    {
        var page = new TabPage("SFTP");
        var table = CreateTable();

        AddTextRow(table, "Hote SFTP", _sftpHost);
        _sftpPort.Minimum = 1;
        _sftpPort.Maximum = 65535;
        _sftpPort.Width = 120;
        AddControlRow(table, "Port SFTP", _sftpPort);
        AddTextRow(table, "Utilisateur SFTP", _sftpUser);
        AddTextRow(table, "Empreinte cle hote SFTP", _sftpFingerprint);

        var hint = new Label
        {
            Text = "Empreinte attendue par le pinning SSH. Exemple: sortie MD5 normalisee sans 'MD5:' ni ':'.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };
        AddControlRow(table, "", hint);

        page.Controls.Add(WrapScrollable(table));
        return page;
    }

    private TabPage BuildAdvancedTab()
    {
        var page = new TabPage("Avance");
        var table = CreateTable();

        AddTextRow(table, "URL heartbeat", _heartbeatUrl);
        AddTextRow(table, "URL serveur de mise a jour", _updateServerUrl);

        _watchPaths.Multiline = true;
        _watchPaths.Height = 90;
        _watchPaths.ScrollBars = ScrollBars.Vertical;
        AddControlRow(table, "Chemins de logs ATM", _watchPaths);

        var hint = new Label
        {
            Text = "Un chemin par ligne. Laissez vide pour activer l'auto-detection des chemins ATM.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 10)
        };
        AddControlRow(table, "", hint);

        page.Controls.Add(WrapScrollable(table));
        return page;
    }

    private Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));

        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Vertical;
        _output.Dock = DockStyle.Fill;
        footer.Controls.Add(_output, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(12, 0, 0, 0)
        };

        var installButton = new Button
        {
            Text = "Installer",
            Width = 220,
            Height = 34
        };
        installButton.Click += (_, _) => RunInstall();

        var configOnlyButton = new Button
        {
            Text = "Generer config seulement",
            Width = 220,
            Height = 34
        };
        configOnlyButton.Click += (_, _) => RunConfigOnly();

        var closeButton = new Button
        {
            Text = "Fermer",
            Width = 220,
            Height = 34
        };
        closeButton.Click += (_, _) => Close();

        buttons.Controls.Add(installButton);
        buttons.Controls.Add(configOnlyButton);
        buttons.Controls.Add(closeButton);
        footer.Controls.Add(buttons, 1, 0);

        return footer;
    }

    private void ApplyDefaults()
    {
        _installPath.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "AtmLogAgent");
        _dataPath.Text = GetDefaultDataPath();
        _serviceName.Text = "AtmLogAgent";

        _bankName.Text = "AUTO";
        _country.Text = "AUTO";
        _city.Text = "AUTO";
        _atmId.Text = "AUTO - resolu par l'agent au demarrage";
        _manufacturer.Text = "NCR";
        _model.Text = "SelfServ";

        _sftpPort.Value = 22;
        _sftpUser.Text = "atm-agent";
        _heartbeatUrl.Text = "https://supervision.example.com/api/heartbeat";
        _updateServerUrl.Text = "https://updates.atm-agent.example.com/api/v1";
    }

    private static TableLayoutPanel CreateTable()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(12)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static Control WrapScrollable(Control content)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        panel.Controls.Add(content);
        return panel;
    }

    private static void AddTextRow(TableLayoutPanel table, string label, TextBox box)
    {
        box.Dock = DockStyle.Fill;
        AddControlRow(table, label, box);
    }

    private static void AddPathRow(TableLayoutPanel table, string label, TextBox box)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        box.Dock = DockStyle.Fill;
        var browse = new Button { Text = "Parcourir", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = box.Text };
            if (dialog.ShowDialog() == DialogResult.OK)
            {
                box.Text = dialog.SelectedPath;
            }
        };
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(browse, 1, 0);
        AddControlRow(table, label, panel);
    }

    private static void AddControlRow(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 8)
        };

        control.Margin = new Padding(0, 5, 0, 5);
        table.Controls.Add(labelControl, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private void RunConfigOnly()
    {
        try
        {
            ValidateInputs(requireService: false);
            GenerateConfiguration(copyBinaries: false);
            AppendLine("Configuration generee avec succes.");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RunInstall()
    {
        try
        {
            ValidateInputs(requireService: _installService.Checked);
            var result = GenerateConfiguration(copyBinaries: true);

            if (_generateSshKey.Checked)
            {
                GenerateSshKey(result.PrivateKeyPath);
            }

            ApplySecureAcl(result.DataPath);

            if (_installService.Checked)
            {
                EnsureAdministrator();
                InstallOrUpdateService(result);
            }

            AppendLine("Installation terminee.");
            AppendLine("Cle publique a declarer cote serveur SFTP:");
            AppendLine(File.Exists(result.PublicKeyPath)
                ? File.ReadAllText(result.PublicKeyPath).Trim()
                : "(cle publique absente)");

            MessageBox.Show(
                "Installation terminee. Copiez la cle publique affichee dans le serveur SFTP client si elle n'est pas encore autorisee.",
                "ATM Log Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private InstallResult GenerateConfiguration(bool copyBinaries)
    {
        var installPath = Required(_installPath, "Repertoire d'installation");
        var dataPath = GetDefaultDataPath();
        var keysPath = Path.Combine(dataPath, "keys");
        var logsPath = Path.Combine(dataPath, "Logs");
        var backupsPath = Path.Combine(dataPath, "Backups");
        var privateKeyPath = Path.Combine(keysPath, "agent_ed25519");
        var configPath = Path.Combine(installPath, "appsettings.json");
        var provisioningPath = Path.Combine(dataPath, "provisioning.conf");

        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(keysPath);
        Directory.CreateDirectory(logsPath);
        Directory.CreateDirectory(backupsPath);

        if (copyBinaries)
        {
            CopyRuntimeFiles(AppContext.BaseDirectory, installPath);
        }

        var watchPaths = _watchPaths.Lines
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var config = new
        {
            AtmAgent = new
            {
                Atm = new
                {
                    BankName = Clean(_bankName.Text, "AUTO"),
                    Country = Clean(_country.Text, "AUTO"),
                    City = Clean(_city.Text, "AUTO"),
                    AtmId = "AUTO",
                    Manufacturer = Clean(_manufacturer.Text, "AUTO"),
                    Model = Clean(_model.Text, "")
                },
                Transmission = new
                {
                    Protocol = "SFTP",
                    Host = Required(_sftpHost, "Hote SFTP"),
                    Port = (int)_sftpPort.Value,
                    Username = Required(_sftpUser, "Utilisateur SFTP"),
                    PrivateKeyPath = privateKeyPath,
                    PrivateKeyPassphrase = (string?)null,
                    RemoteBasePath = "",
                    CompressBeforeTransmit = true,
                    MaxConcurrentTransfers = 3,
                    MaxRetryAttempts = 10,
                    RetryDelaySeconds = 30,
                    FullSyncIntervalHours = 24,
                    ConnectionTimeoutSeconds = 30,
                    KeepAliveIntervalSeconds = 60
                },
                Security = new
                {
                    LocalEncryptionKeyId = Path.Combine(dataPath, "agent.key"),
                    EnableIntegrityChecks = true,
                    EnableTamperDetection = true,
                    ValidateServerCertificate = true,
                    ServerCertificatePinning = NormalizeFingerprint(Required(_sftpFingerprint, "Empreinte cle hote SFTP")),
                    EnableAuditLog = true,
                    AuditLogPath = Path.Combine(logsPath, "audit.log")
                },
                LogDiscovery = new
                {
                    WatchPaths = watchPaths,
                    FilePatterns = new[] { "*.jrn", "*.log", "*.txt", "*.xml", "*.json" },
                    AutoDiscoverAtmPaths = watchPaths.Length == 0,
                    IncludeSubdirectories = true,
                    ExcludedPaths = new[]
                    {
                        @"C:\Windows\System32",
                        dataPath,
                        installPath
                    },
                    PollingIntervalMs = 500
                },
                Update = new
                {
                    UpdateServerUrl = Required(_updateServerUrl, "URL serveur de mise a jour"),
                    UpdatePublicKeyPath = Path.Combine(keysPath, "update_pub.pem"),
                    CheckIntervalHours = 6,
                    EnableAutoUpdate = false,
                    AllowHotReload = false,
                    MaxRollbackVersions = 3
                },
                Monitoring = new
                {
                    HeartbeatUrl = Required(_heartbeatUrl, "URL heartbeat"),
                    HeartbeatIntervalSeconds = 60,
                    ReportDeviceStatuses = true,
                    ReportTransactionStats = true,
                    AlertThresholdBufferSizeMb = 100,
                    AlertThresholdOfflineMinutes = 30
                },
                Retention = new
                {
                    LocalLogRetentionDays = 30,
                    BufferedDataRetentionDays = 7,
                    MaxLocalBufferSizeMb = 500,
                    CompressArchivedLogs = true
                }
            },
            Serilog = new
            {
                MinimumLevel = new
                {
                    Default = "Information",
                    Override = new Dictionary<string, string>
                    {
                        ["Microsoft"] = "Warning",
                        ["System"] = "Warning"
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(provisioningPath, BuildProvisioningFile(), new UTF8Encoding(false));

        AppendLine($"Configuration: {configPath}");
        AppendLine($"Provisioning: {provisioningPath}");

        return new InstallResult(
            ServiceName: Required(_serviceName, "Nom du service"),
            InstallPath: installPath,
            DataPath: dataPath,
            ConfigPath: configPath,
            PrivateKeyPath: privateKeyPath,
            PublicKeyPath: privateKeyPath + ".pub");
    }

    private string BuildProvisioningFile()
    {
        var bank = Clean(_bankName.Text, "AUTO");
        var country = Clean(_country.Text, "AUTO");
        var city = Clean(_city.Text, "AUTO");

        return string.Join(Environment.NewLine, new[]
        {
            "# Fichier genere par AtmLogAgent.SetupWizard",
            $"BankName={bank}",
            $"Country={country}",
            $"City={city}",
            $"BankCode={bank}",
            "Region=AUTO",
            ""
        });
    }

    private void GenerateSshKey(string privateKeyPath)
    {
        if (File.Exists(privateKeyPath) && File.Exists(privateKeyPath + ".pub"))
        {
            AppendLine($"Cle SSH existante conservee: {privateKeyPath}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(privateKeyPath)!);
        var comment = "atm-agent-auto";
        RunProcess("ssh-keygen", "-t", "ed25519", "-f", privateKeyPath, "-N", "", "-C", comment);
        AppendLine($"Cle SSH generee: {privateKeyPath}");
    }

    private void InstallOrUpdateService(InstallResult result)
    {
        var serviceExe = Path.Combine(result.InstallPath, "AtmLogAgent.Service.exe");
        if (!File.Exists(serviceExe))
        {
            throw new FileNotFoundException(
                "AtmLogAgent.Service.exe est introuvable. Lancez l'assistant depuis le dossier publish Windows de l'agent.",
                serviceExe);
        }

        var binPath = $"\"{serviceExe}\" --configdir \"{result.InstallPath}\"";
        var displayName = $"ATM Log Agent - {Clean(_bankName.Text, "AUTO")} AUTO";

        if (ServiceExists(result.ServiceName))
        {
            RunProcessAllowFailure("sc.exe", "stop", result.ServiceName);
            RunProcess("sc.exe", "config", result.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", displayName);
        }
        else
        {
            RunProcess("sc.exe", "create", result.ServiceName, "binPath=", binPath, "start=", "auto", "DisplayName=", displayName);
        }

        RunProcess("sc.exe", "description", result.ServiceName, "Agent de collecte et transmission securisee des journaux ATM.");
        RunProcess("sc.exe", "failure", result.ServiceName, "reset=", "3600", "actions=", "restart/5000/restart/10000/restart/30000");
        SetServiceEnvironment(result.ServiceName, result.DataPath);

        if (_startService.Checked)
        {
            RunProcessAllowFailure("sc.exe", "start", result.ServiceName);
        }

        AppendLine($"Service Windows configure: {result.ServiceName}");
    }

    private static bool ServiceExists(string serviceName)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ArgumentList = { "query", serviceName }
        });
        process!.WaitForExit();
        return process.ExitCode == 0;
    }

    private static void SetServiceEnvironment(string serviceName, string dataPath)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
            writable: true);

        if (key is null)
        {
            throw new InvalidOperationException($"Service introuvable dans le registre: {serviceName}");
        }

        key.SetValue("Environment", new[] { $"ATMAGENT_DATA_DIR={dataPath}" }, RegistryValueKind.MultiString);
    }

    private static void ApplySecureAcl(string dataPath)
    {
        var directory = new DirectoryInfo(dataPath);
        if (!directory.Exists)
        {
            directory.Create();
        }

        var security = directory.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            "BUILTIN\\Administrators",
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            "NT AUTHORITY\\SYSTEM",
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private static void CopyRuntimeFiles(string sourceDir, string installPath)
    {
        var source = new DirectoryInfo(sourceDir);
        var target = new DirectoryInfo(installPath);
        if (string.Equals(
            source.FullName.TrimEnd('\\'),
            target.FullName.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var file in source.GetFiles("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source.FullName, file.FullName);
            if (relative.StartsWith("Logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(target.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            file.CopyTo(destination, overwrite: true);
        }
    }

    private void ValidateInputs(bool requireService)
    {
        Required(_installPath, "Repertoire d'installation");
        Required(_serviceName, "Nom du service");
        Required(_sftpHost, "Hote SFTP");
        Required(_sftpUser, "Utilisateur SFTP");
        var fingerprint = NormalizeFingerprint(Required(_sftpFingerprint, "Empreinte cle hote SFTP"));
        if (fingerprint.Length != 32 || fingerprint.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidOperationException(
                "L'empreinte SFTP doit etre une empreinte MD5 hexadecimale de 32 caracteres, avec ou sans separateurs ':'.");
        }
        Required(_heartbeatUrl, "URL heartbeat");
        Required(_updateServerUrl, "URL serveur de mise a jour");

        if (requireService)
        {
            EnsureAdministrator();
        }
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException(
                "L'installation du service Windows requiert une execution en administrateur.");
        }
    }

    private static string Required(TextBox box, string fieldName)
    {
        var value = box.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Champ obligatoire manquant: {fieldName}");
        }
        return value;
    }

    private static string Clean(string value, string fallback)
    {
        var cleaned = value.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private static string GetDefaultDataPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AtmLogAgent");

    private static string NormalizeFingerprint(string value) =>
        value.Trim()
            .Replace("MD5:", "", StringComparison.OrdinalIgnoreCase)
            .Replace(":", "")
            .Replace("-", "")
            .ToLowerInvariant();

    private static void RunProcess(string fileName, params string[] args) =>
        RunProcess(fileName, args, allowFailure: false);

    private static void RunProcessAllowFailure(string fileName, params string[] args) =>
        RunProcess(fileName, args, allowFailure: true);

    private static void RunProcess(string fileName, string[] args, bool allowFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Impossible de lancer {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0 && !allowFailure)
        {
            throw new InvalidOperationException(
                $"{fileName} a echoue avec le code {process.ExitCode}.{Environment.NewLine}{stdout}{stderr}");
        }
    }

    private void AppendLine(string text)
    {
        _output.AppendText(text + Environment.NewLine);
    }

    private void ShowError(Exception ex)
    {
        AppendLine("ERREUR: " + ex.Message);
        MessageBox.Show(ex.Message, "Erreur d'installation", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private sealed record InstallResult(
        string ServiceName,
        string InstallPath,
        string DataPath,
        string ConfigPath,
        string PrivateKeyPath,
        string PublicKeyPath);
}
