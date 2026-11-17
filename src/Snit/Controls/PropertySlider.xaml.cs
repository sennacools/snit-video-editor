using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace Snit.Controls;

public partial class PropertySlider : UserControl
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(PropertySlider), new PropertyMetadata(""));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PropertySlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(PropertySlider), new PropertyMetadata(0.0));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(PropertySlider), new PropertyMetadata(100.0));

    /// <summary>How the value is written, e.g. "{0:0}%" or "{0:+0.0;-0.0;0.0} dB".</summary>
    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(PropertySlider), new PropertyMetadata("{0:0}", OnDisplayChanged));

    private static readonly DependencyPropertyKey DisplayTextKey = DependencyProperty.RegisterReadOnly(
        nameof(DisplayText), typeof(string), typeof(PropertySlider), new PropertyMetadata("0"));

    public static readonly DependencyProperty DisplayTextProperty = DisplayTextKey.DependencyProperty;

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    public string DisplayText => (string)GetValue(DisplayTextProperty);

    public PropertySlider()
    {
        InitializeComponent();
        UpdateDisplayText();
    }

    private static void OnDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PropertySlider)d).UpdateDisplayText();

    private void UpdateDisplayText()
        => SetValue(DisplayTextKey, string.Format(CultureInfo.CurrentCulture, Format, Value));
}
