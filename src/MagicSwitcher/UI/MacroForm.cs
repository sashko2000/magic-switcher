using MagicSwitcher.Keyboard;
using MagicSwitcher.Localization;

namespace MagicSwitcher.UI;

/// <summary>Macros of the Apple keyboard: a key (alone or with modifiers) types a text. Other keyboards are not affected.</summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class MacroForm : Form
{
    private readonly ListBox _list;
    private readonly CheckBox _ctrl, _alt, _shift, _win;
    private readonly ComboBox _key, _where;
    private readonly TextBox _text;
    private readonly List<KeyMacro> _macros;
    private bool _loading; // the editor is being filled from the list: not an edit

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
        _where = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 440 };
        _where.Items.AddRange([Strings.MacroWhereSession, Strings.MacroWhereBoth, Strings.MacroWhereSignIn]);
        _where.SelectedIndex = 0;
        var whereNote = new Label { Text = Strings.MacroWhereNote, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0) };

        // Editing the fields changes the selected macro right away (no separate "apply" step to forget).
        foreach (var box in new[] { _ctrl, _alt, _shift, _win })
        {
            box.CheckedChanged += (_, _) => ApplyToSelected();
        }

        _key.SelectedIndexChanged += (_, _) => ApplyToSelected();
        _where.SelectedIndexChanged += (_, _) => ApplyToSelected();
        _text.TextChanged += (_, _) => ApplyToSelected();

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
        editor.Controls.Add(new Label { Text = Strings.MacroWhere, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        editor.Controls.Add(_where, 1, 3);
        editor.Controls.Add(whereNote, 1, 4);
        editor.Controls.Add(edit, 1, 5);

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

        FillList(select: _macros.Count > 0 ? 0 : -1); // selected: the fields below edit it
    }

    public static string Describe(KeyMacro macro) =>
        $"{macro.DescribeTrigger()} → {macro.Text}" + (macro.OnSignInScreen
            ? $"  ({(macro.InSession ? Strings.MacroSignInShort : Strings.MacroSignInOnlyShort)})"
            : "");

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
        if (_loading || _list.SelectedIndex < 0 || _list.SelectedIndex >= _macros.Count)
        {
            return;
        }

        var macro = _macros[_list.SelectedIndex];
        _loading = true;
        try
        {
            _ctrl.Checked = macro.Ctrl;
            _alt.Checked = macro.Alt;
            _shift.Checked = macro.Shift;
            _win.Checked = macro.Win;
            _key.SelectedItem = _key.Items.Cast<KeyItem>().FirstOrDefault(k => k.Key == macro.Key) ?? _key.Items[0];
            _text.Text = macro.Text;
            _where.SelectedIndex = !macro.OnSignInScreen ? 0 : macro.InSession ? 1 : 2;
        }
        finally
        {
            _loading = false;
        }
    }

    private KeyMacro FromEditor() => new()
    {
        Key = (_key.SelectedItem as KeyItem)?.Key ?? 0,
        Ctrl = _ctrl.Checked,
        Alt = _alt.Checked,
        Shift = _shift.Checked,
        Win = _win.Checked,
        Text = _text.Text,
        OnSignInScreen = _where.SelectedIndex >= 1,
        InSession = _where.SelectedIndex != 2,
    };

    /// <summary>The fields were edited: change the selected macro (while what is in the fields makes a valid macro).</summary>
    private void ApplyToSelected()
    {
        int index = _list.SelectedIndex;
        if (_loading || index < 0 || index >= _macros.Count)
        {
            return;
        }

        var macro = FromEditor();
        if (!macro.IsValid || macro == _macros[index])
        {
            return;
        }

        _macros[index] = macro;
        _loading = true;
        try
        {
            _list.Items[index] = Describe(macro);
            _list.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Adds the macro from the editor as a new one; one with the same key replaces the old one.</summary>
    private void AddOrReplace()
    {
        var macro = FromEditor();
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
