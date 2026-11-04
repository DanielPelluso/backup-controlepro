namespace BackupControlePro;

/// <summary>Diálogo mínimo para pedir e salvar um segredo em DPAPI.</summary>
public sealed class SecretPromptForm : Form
{
    private readonly TextBox _txt = new();
    private readonly string _label;

    private SecretPromptForm(string title, string label)
    {
        _label = label;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 170);
        Font = new Font("Segoe UI", 9.5f);

        var lbl = new Label
        {
            Text = label,
            Location = new Point(14, 14),
            Size = new Size(432, 60),
        };

        _txt.Location = new Point(14, 84);
        _txt.Size = new Size(432, 26);
        _txt.UseSystemPasswordChar = true;

        var ok = new Button
        {
            Text = "Salvar",
            DialogResult = DialogResult.OK,
            Location = new Point(270, 126),
            Size = new Size(84, 30),
        };
        var cancel = new Button
        {
            Text = "Cancelar",
            DialogResult = DialogResult.Cancel,
            Location = new Point(362, 126),
            Size = new Size(84, 30),
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([lbl, _txt, ok, cancel]);
    }

    public string Secret => _txt.Text;

    /// <summary>Pede o segredo e grava em DPAPI. Devolve false se cancelado.</summary>
    public static bool PromptAndSave(IWin32Window? owner, string title, string label, Action<string> save)
    {
        using var dlg = new SecretPromptForm(title, label);
        if (dlg.ShowDialog(owner) != DialogResult.OK || string.IsNullOrEmpty(dlg.Secret))
            return false;
        save(dlg.Secret);
        return true;
    }
}
