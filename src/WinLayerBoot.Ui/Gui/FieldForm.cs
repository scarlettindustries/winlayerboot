using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinLayerBoot.Core;

namespace WinLayerBoot.Gui;

/// <summary>
/// Draws a form from the field table, shared by operation pages and the settings page: groups are collapsible cards, switches are settings rows with "description on the left, switch on the right", and problems found by checks are shown under the matching input box.
/// When the structure of the field table (keys, kinds, groups) is unchanged, only values, descriptions and candidates are updated and controls are not rebuilt, so focus and text being typed are kept; only when the structure changes (a switch makes extra presets appear / disappear) is everything redrawn.
/// When the user changes a value, raises Changed(key, value, whether to recompute the field table now): switches, dropdown selections and input boxes on losing focus recompute immediately, while the page waits a moment while typing before recomputing.
/// </summary>
internal sealed partial class FieldForm : StackPanel
{
    public event Action<string, string, bool>? Changed;

    private sealed class Row
    {
        public Field F = null!;
        public FrameworkElement Ctl = null!;
        public TextBlock? Help;
        public StackPanel Problem = null!;
        public TextBlock ProblemText = null!;
        public FontIcon ProblemIcon = null!;
        public string Shown = "";   // Values filled in by the program: TextChanged fires asynchronously, so this is used to tell whether the user changed it
        public bool Dirty;          // Typed into, not yet left
    }

    private readonly Dictionary<string, Row> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _collapsed = new();   // Collapsed groups, kept when the structure changes and the form is redrawn
    private string _shape = "";
    private bool _updating;

    public FieldForm() => Spacing = 12;

    public void Show(IReadOnlyList<Field> fields, Values values)
    {
        var shape = string.Join("|", fields.Select(f => $"{f.Key}:{f.Kind}:{f.Group}:{f.Strict}:{f.Help != ""}"));
        _updating = true;
        try
        {
            if (shape != _shape) Rebuild(fields, values);
            else foreach (var f in fields) Update(_rows[f.Key], f, values.Get(f.Key));
            _shape = shape;
        }
        finally { _updating = false; }
    }

    /// <summary>Shows the problems found by checks under the matching input boxes, and clears those with no problem.</summary>
    public void ShowProblems(CheckList checks)
    {
        foreach (var r in _rows.Values)
        {
            var p = checks.ProblemOf(r.F.Key);
            r.Problem.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
            if (p is null) continue;
            r.ProblemText.Text = $"{p.Item}: {p.Detail}";
            r.ProblemText.Foreground = r.ProblemIcon.Foreground = Ui.StateBrush(p.StateText);
            r.ProblemIcon.Glyph = p.State == CheckState.Fail ? Ui.GlyphError : Ui.GlyphWarn;
        }
    }

    /// <summary>Jumps from the problem list to the matching input box.</summary>
    public void FocusField(string key)
    {
        if (!_rows.TryGetValue(key, out var r)) return;
        r.Ctl.StartBringIntoView();
        if (r.Ctl is Control c) c.Focus(FocusState.Programmatic);
    }

    private void Raise(string key, string val, bool rebuild)
    {
        if (!_updating) Changed?.Invoke(key, val, rebuild);
    }

    private void Rebuild(IReadOnlyList<Field> fields, Values values)
    {
        Children.Clear();
        _rows.Clear();
        foreach (var g in fields.GroupBy(f => f.Group))
        {
            var body = new StackPanel { Spacing = 12 };
            foreach (var f in g) body.Children.Add(MakeRow(f, values.Get(f.Key)));
            if (g.Key == "") { Children.Add(Ui.Card(body)); continue; }
            var key = g.Key;
            var ex = new Expander
            {
                Header = key, Content = body, IsExpanded = !_collapsed.GetValueOrDefault(key),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ex.Expanding += (_, _) => _collapsed[key] = false;
            ex.Collapsed += (_, _) => _collapsed[key] = true;
            Children.Add(ex);
        }
    }

    private StackPanel MakeRow(Field f, string val)
    {
        var row = new Row { F = f, Shown = val };
        _rows[f.Key] = row;
        row.Ctl = Make(row, f);
        var box = new StackPanel { Spacing = 4 };
        if (f.Kind == FieldKind.Bool)
        {
            // Settings row: description on the left, switch on the right (like Windows Settings)
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            left.Children.Add(Ui.Text(f.Label));
            if (f.Help != "") left.Children.Add(row.Help = Ui.Text(f.Help, 12, dim: true));
            g.Children.Add(left);
            Grid.SetColumn(row.Ctl, 1);
            g.Children.Add(row.Ctl);
            box.Children.Add(g);
        }
        else
        {
            box.Children.Add(row.Ctl);
            if (f.Help != "" && f.Kind != FieldKind.Note) box.Children.Add(row.Help = Ui.Text(f.Help, 12, dim: true));
        }
        row.ProblemIcon = new FontIcon { FontSize = 12, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };
        row.ProblemText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        row.Problem = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        row.Problem.Children.Add(row.ProblemIcon);
        row.Problem.Children.Add(row.ProblemText);
        box.Children.Add(row.Problem);
        return box;
    }

    private FrameworkElement Make(Row r, Field f)
    {
        var key = f.Key;
        var val = r.Shown;
        switch (f.Kind)
        {
            case FieldKind.Note:
                return Ui.Text($"{f.Label}: {f.Default}", dim: true);
            case FieldKind.Bool:
            {
                var ts = new ToggleSwitch { OnContent = "", OffContent = "", IsOn = val.IsYes, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
                AutomationName(ts, f.Label);
                ts.Toggled += (_, _) => Raise(key, ts.IsOn ? "Y" : "N", true);
                return ts;
            }
            case FieldKind.Choice:
            {
                var cb = new ComboBox { Header = f.Label, IsEditable = !f.Strict, HorizontalAlignment = HorizontalAlignment.Stretch };
                SetChoices(cb, f, val);
                cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != r.Shown) { r.Shown = s; Raise(key, s, true); } };
                if (!f.Strict)
                {
                    cb.TextSubmitted += (_, e) => { if (e.Text != r.Shown) { r.Shown = e.Text; Raise(key, e.Text, true); } };
                    cb.LostFocus += (_, _) => { if (!cb.IsDropDownOpen && cb.Text != r.Shown) { r.Shown = cb.Text; Raise(key, cb.Text, true); } };
                }
                return cb;
            }
            case FieldKind.Int:
            {
                var nb = new NumberBox { Header = f.Label, Value = int.TryParse(val, out var n) ? n : 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = 0, SmallChange = 1, LargeChange = 16 };
                nb.ValueChanged += (_, _) =>
                {
                    var s = double.IsNaN(nb.Value) ? "0" : ((long)nb.Value).ToString();
                    if (s != r.Shown) { r.Shown = s; Raise(key, s, false); }
                };
                return nb;
            }
            case FieldKind.Password:
            {
                var pb = new PasswordBox { Header = f.Label, Password = val, PasswordRevealMode = PasswordRevealMode.Peek };
                pb.PasswordChanged += (_, _) => { if (pb.Password != r.Shown) { r.Shown = pb.Password; Raise(key, pb.Password, false); } };
                return pb;
            }
            default:
            {
                var tb = new TextBox { Header = f.Label, Text = val };
                tb.TextChanged += (_, _) => { if (tb.Text != r.Shown) { r.Shown = tb.Text; r.Dirty = true; Raise(key, tb.Text, false); } };
                tb.LostFocus += (_, _) => { if (r.Dirty) { r.Dirty = false; Raise(key, tb.Text, true); } };
                return tb;
            }
        }
    }

    private static void AutomationName(DependencyObject o, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    private static void SetChoices(ComboBox cb, Field f, string val)
    {
        cb.Items.Clear();
        foreach (var c in f.Choices) cb.Items.Add(c);
        if (f.Choices.Contains(val)) cb.SelectedItem = val;
        else if (!f.Strict) cb.Text = val;
    }

    private void Update(Row r, Field f, string val)
    {
        var old = r.F;
        r.F = f;
        if (r.Help is not null && r.Help.Text != f.Help) r.Help.Text = f.Help;
        var focused = r.Ctl is Control c && c.FocusState != FocusState.Unfocused;
        switch (r.Ctl)
        {
            case TextBlock note:
                note.Text = $"{f.Label}: {f.Default}";
                return;
            case ToggleSwitch ts:
                r.Shown = val;
                if (ts.IsOn != val.IsYes) ts.IsOn = val.IsYes;
                return;
            case ComboBox cb:
                if (focused || cb.IsDropDownOpen) return;
                r.Shown = val;
                if (!old.Choices.SequenceEqual(f.Choices)) SetChoices(cb, f, val);
                else if (f.Choices.Contains(val)) { if (!Equals(cb.SelectedItem, val)) cb.SelectedItem = val; }
                else if (!f.Strict && cb.Text != val) { cb.SelectedItem = null; cb.Text = val; }
                return;
            case NumberBox nb:
                if (focused || !int.TryParse(val, out var n)) return;
                r.Shown = val;
                if (nb.Value != n) nb.Value = n;
                return;
            case PasswordBox pb:
                if (focused) return;
                r.Shown = val;
                if (pb.Password != val) pb.Password = val;
                return;
            case TextBox tb:
                if (focused || r.Dirty) return;
                r.Shown = val;
                if (tb.Text != val) tb.Text = val;
                return;
        }
    }
}
