namespace BackupControlePro;

public sealed class MainForm : Form
{
    private readonly Button _btnBackup = new();
    private readonly Label _lblLast = new();
    private readonly GroupBox _grpRestore = new();
    private readonly ListBox _lstBackups = new();
    private readonly RadioButton _rbData = new() { Text = "DADOS (truncate + reload)", Checked = true };
    private readonly RadioButton _rbFull = new() { Text = "TUDO (schema + dados)" };
    private readonly CheckBox _chkDb = new() { Text = "Restaurar banco", Checked = true };
    private readonly CheckBox _chkPhotos = new() { Text = "Restaurar fotos" };
    private readonly Button _btnRestore = new();
    private readonly Button _btnRefresh = new();
    private readonly TextBox _txtLog = new();
    private readonly Button _btnOpenFolder = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Pronto" };

    private bool _busy;
    private bool _storageAvailable;

    public MainForm()
    {
        Text = "Backup ControlePro";
        Size = new Size(820, 680);
        MinimumSize = new Size(740, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);

        // --- backup ---
        _btnBackup.Text = "Fazer backup agora (banco + fotos)";
        _btnBackup.Font = new Font(Font, FontStyle.Bold);
        _btnBackup.SetBounds(16, 16, 300, 44);
        _btnBackup.Click += async (_, _) => await RunBackupAsync();

        _lblLast.Text = "Último backup: —";
        _lblLast.SetBounds(328, 16, 454, 44);
        _lblLast.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _lblLast.TextAlign = ContentAlignment.MiddleLeft;

        // --- restaurar ---
        _grpRestore.Text = "Restaurar (backups na pasta)";
        _grpRestore.SetBounds(16, 72, 772, 248);
        _grpRestore.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        _lstBackups.SetBounds(16, 24, 520, 204);
        _lstBackups.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        _lstBackups.IntegralHeight = false;
        _lstBackups.HorizontalScrollbar = false;

        _rbData.SetBounds(548, 26, 208, 22);
        _rbData.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _rbFull.SetBounds(548, 50, 208, 22);
        _rbFull.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _chkDb.SetBounds(548, 78, 208, 22);
        _chkDb.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _chkPhotos.SetBounds(548, 104, 208, 22);
        _chkPhotos.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        _btnRefresh.Text = "Atualizar";
        _btnRefresh.SetBounds(548, 134, 100, 30);
        _btnRefresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnRefresh.Click += (_, _) => RefreshList();

        _btnRestore.Text = "Restaurar...";
        _btnRestore.SetBounds(656, 134, 100, 30);
        _btnRestore.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _btnRestore.Enabled = false;
        _btnRestore.Click += async (_, _) => await RunRestoreAsync();

        _grpRestore.Controls.AddRange(
            [_lstBackups, _rbData, _rbFull, _chkDb, _chkPhotos, _btnRefresh, _btnRestore]);

        // --- log ---
        _txtLog.SetBounds(16, 332, 772, 250);
        _txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _txtLog.Multiline = true;
        _txtLog.ReadOnly = true;
        _txtLog.ScrollBars = ScrollBars.Vertical;
        _txtLog.Font = new Font("Consolas", 9f);
        _txtLog.BackColor = Color.FromArgb(24, 24, 24);
        _txtLog.ForeColor = Color.FromArgb(220, 220, 220);

        _btnOpenFolder.Text = "Abrir pasta de backups";
        _btnOpenFolder.SetBounds(16, 594, 170, 28);
        _btnOpenFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _btnOpenFolder.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppConfig.BackupRoot,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex) { Log($"Falha ao abrir pasta: {ex.Message}"); }
        };

        _status.Items.Add(_statusLabel);

        Controls.AddRange([_btnBackup, _lblLast, _grpRestore, _txtLog, _btnOpenFolder, _status]);

        _chkDb.CheckedChanged += (_, _) => UpdateRestoreEnabled();
        _chkPhotos.CheckedChanged += (_, _) => UpdateRestoreEnabled();
        _lstBackups.SelectedIndexChanged += (_, _) => UpdateRestoreEnabled();

        Shown += (_, _) => OnShown();
    }

    private void OnShown()
    {
        try
        {
            AppConfig.ValidateTools();
            Log($"Ferramentas OK · raiz: {AppConfig.BackupRoot}");
        }
        catch (Exception ex)
        {
            Log($"ERRO de ferramentas: {ex.Message}");
            _btnBackup.Enabled = false;
            _btnRestore.Enabled = false;
            _statusLabel.Text = "Ferramentas PostgreSQL ausentes";
            RefreshList();
            return;
        }

        if (!AppConfig.HasDbPassword)
        {
            if (SecretPromptForm.PromptAndSave(
                    this,
                    "Senha do banco",
                    "Informe a senha do banco Supabase (CONTROLEPRO_SUPABASE_DB_PASSWORD).\n" +
                    "Ela fica salva criptografada (DPAPI) ao lado do EXE.",
                    AppConfig.SaveDbPassword))
            {
                Log("Senha do banco salva (DPAPI).");
            }
            else
            {
                _btnBackup.Enabled = false;
                _btnRestore.Enabled = false;
                _statusLabel.Text = "Sem senha do banco";
                Log("AVISO: senha não informada — backup/restauração desativados.");
                RefreshList();
                return;
            }
        }

        if (!AppConfig.HasAccessToken)
        {
            if (SecretPromptForm.PromptAndSave(
                    this,
                    "Token do Storage",
                    "Informe o token do Management API (SUPABASE_ACCESS_TOKEN).\n" +
                    "Ele é usado só para baixar/restaurar as fotos; fica salvo em DPAPI ao lado do EXE.",
                    AppConfig.SaveAccessToken))
            {
                _storageAvailable = true;
                Log("Token do Storage salvo (DPAPI).");
            }
            else
            {
                _storageAvailable = false;
                _btnBackup.Enabled = false;
                _statusLabel.Text = "Sem token do Storage";
                Log("AVISO: token do Storage não informado — backup de fotos indisponível.");
            }
        }
        else
        {
            _storageAvailable = true;
        }

        RefreshList();
    }

    private void RefreshList()
    {
        try
        {
            var items = RestoreEngine.ListAll();
            _lstBackups.Items.Clear();
            foreach (var e in items)
                _lstBackups.Items.Add(e);
            _grpRestore.Text = items.Count == 1
                ? "Restaurar (1 backup na pasta)"
                : $"Restaurar ({items.Count} backups na pasta)";
            _statusLabel.Text = items.Count == 0
                ? "Nenhum backup encontrado nesta pasta"
                : $"{items.Count} backup(s) — role a lista para ver os mais antigos";
        }
        catch (Exception ex)
        {
            Log($"ERRO ao listar backups: {ex.Message}");
        }
    }

    private void UpdateRestoreEnabled()
    {
        _rbData.Enabled = !_busy && _chkDb.Checked;
        _rbFull.Enabled = !_busy && _chkDb.Checked;
        _btnRestore.Enabled =
            !_busy &&
            _lstBackups.SelectedItem != null &&
            (_chkDb.Checked || _chkPhotos.Checked);
    }

    private async Task RunBackupAsync()
    {
        if (_busy) return;

        if (!_storageAvailable && !AppConfig.HasAccessToken)
        {
            if (!SecretPromptForm.PromptAndSave(
                    this,
                    "Token do Storage",
                    "Informe o token do Management API para o backup das fotos.",
                    AppConfig.SaveAccessToken))
            {
                Log("backup cancelado: sem token do Storage");
                return;
            }
            _storageAvailable = true;
        }

        SetBusy(true, "Backup em andamento...");
        try
        {
            var result = await Task.Run(() => BackupEngine.Run(true, Log));
            if (result.Success)
            {
                _lblLast.Text =
                    $"Último backup: {result.Stamp} ({result.Elapsed.TotalSeconds:F1}s, " +
                    $"{result.StorageFiles} fotos)";
                _statusLabel.Text = $"Backup OK · {result.Stamp}";
                RefreshList();
            }
            else
            {
                _statusLabel.Text = "Backup FALHOU";
                MessageBox.Show(this,
                    $"Backup falhou:\n\n{result.Error}",
                    "Backup ControlePro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            Log($"ERRO: {ex.Message}");
            _statusLabel.Text = "Backup FALHOU";
            MessageBox.Show(this, ex.Message, "Backup ControlePro",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RunRestoreAsync()
    {
        if (_busy) return;
        if (_lstBackups.SelectedItem is not BackupEntry entry)
            return;

        var restoreDb = _chkDb.Checked;
        var restorePhotos = _chkPhotos.Checked;
        if (!restoreDb && !restorePhotos) return;

        if (restorePhotos && !entry.HasStorage)
        {
            MessageBox.Show(this,
                $"O backup {entry.Stamp} não contém fotos.\n\n" +
                "Desmarque \"Restaurar fotos\" ou escolha outro backup.",
                "Backup ControlePro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var scope = restoreDb
            ? (_rbFull.Checked ? "banco TUDO (schema + dados)" : "banco DADOS (truncate + reload)")
            : "banco ignorado";
        if (restorePhotos) scope += " + FOTOS";

        var warn =
            $"ATENÇÃO: restauração DESTRUTIVA em produção.\n\n" +
            $"Backup: {entry.Stamp}  ({entry.DisplayWhen})\n" +
            $"Escopo: {scope}\n\n" +
            $"Um backup de segurança do estado ATUAL será feito antes" +
            (restorePhotos ? " (banco + fotos)." : " (banco).") +
            "\n\nDeseja continuar?";

        if (MessageBox.Show(this, warn, "Confirmar restauração",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            Log("restauração cancelada pelo usuário");
            return;
        }

        SetBusy(true, "Restaurando...");
        try
        {
            await Task.Run(() =>
            {
                Log("=== PRE-RESTORE (estado atual) ===");
                var pre = BackupEngine.Run(restorePhotos, Log);
                if (!pre.Success)
                    throw new InvalidOperationException(
                        $"pre-restore falhou — restauração ABORTADA: {pre.Error}");
                Log($"pre-restore OK ({pre.Stamp})");

                if (restoreDb)
                {
                    Log($"=== RESTAURANDO {entry.Stamp} (banco) ===");
                    if (_rbFull.Checked)
                        RestoreEngine.RestoreFull(entry, Log);
                    else
                        RestoreEngine.RestoreData(entry, Log);
                    RestoreEngine.CheckSequences(Log);
                }

                if (restorePhotos)
                {
                    Log($"=== RESTAURANDO {entry.Stamp} (fotos) ===");
                    var s = StorageRestoreEngine.Run(entry.Folder, Log);
                    if (!s.Success)
                        throw new InvalidOperationException(
                            $"restore de fotos falhou: {s.Error}\n\n" +
                            "O banco já restaurado (se selecionado) permanece; " +
                            "o pre-restore está na pasta raiz.");
                }
            });

            _statusLabel.Text = "Restauração concluída";
            Log("=== RESTAURAÇÃO CONCLUÍDA ===");
            RefreshList();
            MessageBox.Show(this,
                "Restauração concluída com sucesso.",
                "Backup ControlePro", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Restauração FALHOU";
            Log($"ERRO: {ex.Message}");
            MessageBox.Show(this,
                $"Restauração falhou:\n\n{ex.Message}\n\n" +
                "O pre-restore (estado anterior) está na pasta raiz.",
                "Backup ControlePro", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            RefreshList();
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        _btnBackup.Enabled = !busy && _storageAvailable;
        _btnRefresh.Enabled = !busy;
        _lstBackups.Enabled = !busy;
        _rbData.Enabled = !busy && _chkDb.Checked;
        _rbFull.Enabled = !busy && _chkDb.Checked;
        _chkDb.Enabled = !busy;
        _chkPhotos.Enabled = !busy;
        UpdateRestoreEnabled();
        if (status != null) _statusLabel.Text = status;
        UseWaitCursor = busy;
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(msg));
            return;
        }
        _txtLog.AppendText(line + Environment.NewLine);
    }
}
