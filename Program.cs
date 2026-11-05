namespace BackupControlePro;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Modo CLI (teste/automação):
        //   --backup [--no-storage]
        //   --list
        //   --restore data|full <stamp> [--with-storage] [--dry-run] --yes
        if (args.Length > 0)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
            catch { /* sem console válido (ex.: duplo clique) — segue com o encoding padrão */ }
            ApplicationConfiguration.Initialize();
            return RunCli(args);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunCli(string[] args)
    {
        void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
        try
        {
            AppConfig.ValidateTools();
            switch (args[0])
            {
                case "--backup":
                {
                    var includeStorage = !args.Contains("--no-storage");
                    AppConfig.ValidateDbPassword();
                    if (includeStorage) AppConfig.ValidateAccessToken();

                    var r = BackupEngine.Run(includeStorage, Log);
                    Console.WriteLine(r.Success
                        ? $"BACKUP_OK {r.Stamp} {r.Folder} storage={r.StorageFiles}/{r.StorageBytes}"
                        : $"BACKUP_FAIL {r.Error}");
                    return r.Success ? 0 : 1;
                }
                case "--list":
                {
                    foreach (var e in RestoreEngine.ListAll())
                        Console.WriteLine(
                            $"{e.Stamp} | {e.DisplayWhen} | data={e.DataBytes} custom={e.CustomBytes}" +
                            (e.HasStorage ? $" fotos={e.StorageFiles}/{e.StorageBytes}" : ""));
                    return 0;
                }
                case "--restore" when args.Length >= 3:
                {
                    var scope = args[1]; // data | full
                    var stamp = args[2];
                    var yes = args.Contains("--yes");
                    var dryRun = args.Contains("--dry-run");
                    var withStorage = args.Contains("--with-storage");
                    if (dryRun)
                    {
                        Log("DRY-RUN: nada será aplicado (transação revertida ao final)");
                    }
                    else if (!yes)
                    {
                        Console.WriteLine("RECUSADO: falta --yes (restauração destrutiva)");
                        return 2;
                    }

                    AppConfig.ValidateDbPassword();
                    if (withStorage && !dryRun) AppConfig.ValidateAccessToken();

                    var entry = RestoreEngine.ListAll()
                        .FirstOrDefault(e => e.Stamp == stamp)
                        ?? throw new InvalidOperationException($"backup {stamp} não encontrado");
                    if (withStorage && !entry.HasStorage)
                        throw new InvalidOperationException($"backup {stamp} não contém fotos");

                    if (dryRun && withStorage)
                        Log("aviso: --dry-run ignora a restauração de fotos");

                    if (!dryRun)
                    {
                        Log("=== PRE-RESTORE ===");
                        var pre = BackupEngine.Run(withStorage, Log);
                        if (!pre.Success)
                        {
                            Log($"pre-restore FALHOU: {pre.Error}");
                            return 1;
                        }
                        Log($"pre-restore OK ({pre.Stamp})");
                    }

                    Log($"=== {(dryRun ? "DRY-RUN " : "")}RESTAURANDO {stamp} ({scope}{(withStorage && !dryRun ? " + fotos" : "")}) ===");
                    if (scope == "full") RestoreEngine.RestoreFull(entry, Log, dryRun);
                    else RestoreEngine.RestoreData(entry, Log, dryRun);
                    if (!dryRun) RestoreEngine.CheckSequences(Log);

                    if (withStorage && !dryRun)
                    {
                        var s = StorageRestoreEngine.Run(entry.Folder, Log);
                        if (!s.Success) throw new InvalidOperationException(s.Error ?? "restore de fotos falhou");
                    }

                    Log(dryRun ? "DRYRUN_OK" : "RESTORE_OK");
                    return 0;
                }
                default:
                    Console.WriteLine(
                        "Uso: --backup [--no-storage] | --list | " +
                        "--restore data|full <stamp> [--with-storage] [--dry-run] --yes");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERRO: {ex.Message}");
            return 1;
        }
    }
}
