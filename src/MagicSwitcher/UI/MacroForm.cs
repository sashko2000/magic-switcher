using MagicSwitcher.Keyboard;
using MagicSwitcher.Localization;

namespace MagicSwitcher.UI;

/// <summary>Macros of the Apple keyboard: a key (alone or with modifiers) types a text. Other keyboards are not affected.</summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class MacroForm : Form
{
    private readonly ListBox _list;
    private readonly CheckBox _ctrl, _alt, _shift, _win, _signIn;
    private readonly ComboBox _key;
    private readonly TextBox _text;
    private readonly List<KeyMacro> _macros;

    /// <summary>The macros when the user clicked OK.</summary>
    public List<KeyMacro> Result { get; private set; }

    public MacroForm(IReadOnlyCollection<KeyMacro> current)
    {
        _macros = current.Where(m => m.IsValid).ToList();
        Result = _macros.ToList();
        Text = Strings.MacroTitle;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var note = new Label { Text = Strings.MacroNote, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(3, 3, 3, 10) };

        _list = new ListBox { Width = 560, Height = 110, IntegralHeight = false };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();

        _ctrl = new CheckBox { Text = "Control", AutoSize = true };
        _alt = new CheckBox { Text = "Option", AutoSize = true };
        _shift = new CheckBox { Text = "Shift", AutoSize = true };
        _win = new CheckBox { Text = "Command", AutoSize = true };
        _key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
        foreach (int key in KeyMacro.FunctionKeys)
        {
            _key.Items.Add(new KeyItem(key));
        }

        for (int key = 'A'; key <= 'Z'; key++)
        {
            _key.Items.Add(new KeyItem(key));
        }

        for (int key = '0'; key <= '9'; key++)
        {
            _key.Items.Add(new KeyItem(key));
        }

        _key.SelectedIndex = 0;

        var trigger = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        trigger.Controls.AddRange([_ctrl, _alt, _shift, _win, _key]);

        _text = new TextBox { Width = 440, MaxLength = KeyMacro.MaxTextLength };
        var tokens = new Label { Text = Strings.MacroTokens, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) };
        _signIn = new CheckBox { Text = Strings.MacroSignIn, AutoSize = true };

        var add = new Button { Text = Strings.MacroAdd, AutoSize = true };
        add.Click += (_, _) => AddOrReplace();
        var remove = new Button { Text = Strings.MacroRemove, AutoSize = true };
        remove.Click += (_, _) => RemoveSelected();
        var edit = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        edit.Controls.AddRange([add, remove]);

        var editor = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        editor.Controls.Add(new Label { Text = Strings.MacroKey, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        editor.Controls.Add(trigger, 1, 0);
        editor.Controls.Add(new Label { Text = Strings.MacroText, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        editor.Controls.Add(_text, 1, 1);
        editor.Controls.Add(tokens, 1, 2);
        editor.Controls.Add(_signIn, 1, 3);
        editor.Controls.Add(edit, 1, 4);

        var ok = new Button { Text = Strings.SettingsSave, AutoSize = true, DialogResult = DialogResult.OK };
        ok.Click += (_, _) => Result = _macros.ToList();
        var cancel = new Button { Text = Strings.SettingsCancel, AutoSize = true, DialogResult = DialogResult.Cancel };
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(12) };
        root.Controls.Add(note, 0, 0);
        root.Controls.Add(_list, 0, 1);
        root.Controls.Add(editor, 0, 2);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);

        FillList(select: -1);
    }

    public static string Describe(KeyMacro macro) =>
        $"{macro.DescribeTrigger()} → {macro.Text}" + (macro.OnSignInScreen ? $"  ({Strings.MacroSignInShort})" : "");

    private void FillList(int select)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var macro in _macros)
        {
            _list.Items.Add(Describe(macro));
        }

        _list.EndUpdate();
        if (select >= 0 && select < _list.Items.Count)
        {
            _list.SelectedIndex = select;
        }
    }

    private void LoadSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _macros.Count)
        {
            return;
        }

        var macro = _macros[_list.SelectedIndex];
        _ctrl.Checked = macro.Ctrl;
        _alt.Checked = macro.Alt;
        _shift.Checked = macro.Shift;
        _win.Checked = macro.Win;
        _key.SelectedItem = _key.Items.Cast<KeyItem>().FirstOrDefault(k => k.Key == macro.Key) ?? _key.Items[0];
        _text.Text = macro.Text;
        _signIn.Checked = macro.OnSignInScreen;
    }

    /// <summary>Adds the macro from the editor; one with the same key replaces the old one.</summary>
    private void AddOrReplace()
    {
        var macro = new KeyMacro
        {
            Key = (_key.SelectedItem as KeyItem)?.Key ?? 0,
            Ctrl = _ctrl.Checked,
            Alt = _alt.Checked,
            Shift = _shift.Checked,
            Win = _win.Checked,
            Text = _text.Text,
            OnSignInScreen = _signIn.Checked,
        };
        if (!macro.IsValid)
        {
            MessageBox.Show(this, Strings.MacroInvalid, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int existing = _macros.FindIndex(m => m.DescribeTrigger() == macro.DescribeTrigger());
        if (existing >= 0)
        {
            _macros[existing] = macro;
        }
        else if (_macros.Count >= KeyMacro.MaxCount)
        {
            MessageBox.Show(this, Strings.MacroTooMany(KeyMacro.MaxCount), Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        else
        {
            _macros.Add(macro);
            existing = _macros.Count - 1;
        }

        FillList(existing);
    }

    private void RemoveSelected()
    {
        int index = _list.SelectedIndex;
        if (index >= 0 && index < _macros.Count)
        {
            _macros.RemoveAt(index);
            FillList(Math.Min(index, _macros.Count - 1));
        }
    }

    private sealed record KeyItem(int Key)
    {
        public override string ToString() => Key is >= 0x70 and <= 0x7B ? $"F{Key - 0x6F}" : ((char)Key).ToString();
    }
}
