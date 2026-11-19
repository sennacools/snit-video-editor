using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Snit.Core;

namespace Snit.ViewModels;

/// <summary>
/// One effect in the inspector's Effects section: its name, on/off, remove, its sliders and
/// (for green screen and LUT) a colour or file. Changes go back to the clip through
/// <see cref="change"/>, which makes them undoable.
/// </summary>
public partial class EffectRowViewModel : ObservableObject
{
    private readonly Func<ClipEffect?> current;
    private readonly Action<string, Func<ClipEffect, ClipEffect>> change;
    private readonly Action remove;

    public EffectRowViewModel(ClipEffect effect, Func<ClipEffect?> current,
        Action<string, Func<ClipEffect, ClipEffect>> change, Action remove)
    {
        Id = effect.Id;
        this.current = current;
        this.change = change;
        this.remove = remove;
        Definition = EffectCatalog.Find(effect.Kind)!;
        Parameters = new(Definition.Parameters.Select(p => new EffectParameterViewModel(this, p)));
    }

    public Guid Id { get; }
    public EffectDefinition Definition { get; }
    public string Name => Definition.Name;
    public ObservableCollection<EffectParameterViewModel> Parameters { get; }

    public bool NeedsColor => Definition.TextKind == "color";
    public bool NeedsFile => Definition.TextKind == "lut";

    public bool Enabled
    {
        get => current()?.Enabled ?? true;
        set => change("enabled", e => e with { Enabled = value });
    }

    /// <summary>The key colour (green screen), as #RRGGBB.</summary>
    public string Color
    {
        get => current()?.Text ?? "#00FF00";
        set
        {
            string c = value.Trim();
            if (!c.StartsWith('#')) c = "#" + c;
            if (System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$")) change("color", e => e with { Text = c.ToUpperInvariant() });
            else OnPropertyChanged();
        }
    }

    /// <summary>The LUT file's name (or a hint when none is chosen yet).</summary>
    public string FileName => current()?.Text is string path && path.Length > 0 ? System.IO.Path.GetFileName(path) : "No .cube file chosen";

    public double Value(string name) => current()?[name] ?? 0;

    public void SetValue(string name, double value) => change("value:" + name, e => e.With(name, value));

    [RelayCommand]
    private void Remove() => remove();

    [RelayCommand]
    private void SetColor(string color) => Color = color;

    [RelayCommand]
    private void ChooseFile()
    {
        var dialog = new OpenFileDialog { Title = "Choose a LUT", Filter = "LUT files (*.cube)|*.cube|All files|*.*" };
        if (dialog.ShowDialog() == true) change("file", e => e with { Text = dialog.FileName });
    }

    /// <summary>Re-read every value (after undo, or a change elsewhere).</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var p in Parameters) p.Refresh();
    }
}

/// <summary>One slider (or switch) of an effect.</summary>
public partial class EffectParameterViewModel(EffectRowViewModel row, EffectParameter parameter) : ObservableObject
{
    public string Label => parameter.Label;
    public double Minimum => parameter.Min;
    public double Maximum => parameter.Max;
    public string Format => parameter.Format;
    public bool IsSwitch => parameter.IsSwitch;
    public bool IsChoice => parameter.IsChoice;
    public bool IsSlider => !parameter.IsSwitch && !parameter.IsChoice;
    public IReadOnlyList<string> Options => parameter.Options ?? [];

    /// <summary>The chosen option of a dropdown setting.</summary>
    public int SelectedIndex
    {
        get => (int)Math.Round(row.Value(parameter.Name));
        set { if (value >= 0) row.SetValue(parameter.Name, value); }
    }

    public double Value
    {
        get => row.Value(parameter.Name);
        set => row.SetValue(parameter.Name, Math.Round(value, parameter.Max - parameter.Min <= 10 ? 2 : 1));
    }

    public bool IsOn
    {
        get => row.Value(parameter.Name) >= 0.5;
        set => row.SetValue(parameter.Name, value ? 1 : 0);
    }

    public void Refresh() => OnPropertyChanged(string.Empty);
}
