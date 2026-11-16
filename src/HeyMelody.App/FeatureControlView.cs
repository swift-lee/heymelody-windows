using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.MarkupExtensions.CompiledBindings;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Windows presentation for the small, explicitly supported value-control vocabulary.</summary>
/// <remarks>
/// Each control is itself a settings row, so its section lays it out like any native expander item. A refreshed
/// definition with the same shape keeps the editor instance (and keyboard focus) and only swaps its data context.
/// </remarks>
public sealed class FeatureControlView : FASettingsExpanderItem
{
    private FeatureControlViewModel? _model;
    private string? _shape;
    private readonly List<Control> _boundEditors = [];

    public FeatureControlView() => DataContextChanged += (_, _) => Present();
    protected override Type StyleKeyOverride => typeof(FASettingsExpanderItem);

    /// <summary>The model whose values this row currently shows and submits.</summary>
    internal FeatureControlViewModel? Model => _model;

    private void Present()
    {
        if (DataContext is not FeatureControlViewModel model) { _model = null; _shape = null; Hide(); return; }
        _model = model;
        var shape = Shape(model);
        if (shape != _shape || Footer is null)
        {
            if (!Build(model)) { _shape = null; Hide(); return; }
            _shape = shape;
        }
        else foreach (var editor in _boundEditors) editor.DataContext = model;
        Content = model.Label;
        // An empty description still reserves a caption line and lifts the label off-centre.
        Description = string.IsNullOrEmpty(model.Description) ? null : model.Description;
        if (Footer is Control footer) Avalonia.Automation.AutomationProperties.SetName(footer, model.Label);
        IsVisible = true;
    }

    // Everything that decides which editor exists and how it is configured; values are bound and may change freely.
    private static string Shape(FeatureControlViewModel model) => string.Join("|", model.Id, model.Kind, model.Minimum, model.Maximum,
        model.Step, model.Definition.Value is null, string.Join(",", model.Options.Select(option => option.Key + ":" + option.Name)));

    private bool Build(FeatureControlViewModel model)
    {
        _boundEditors.Clear();
        Control editor;
        switch (model.Kind)
        {
            case "Switch":
                var toggle = new ToggleSwitch { Classes = { "setting" } };
                toggle.Bind(ToggleSwitch.IsCheckedProperty, Property(nameof(model.BooleanValue), value => value.BooleanValue));
                toggle.Click += async (_, _) =>
                {
                    if (_model is not { } current) return;
                    current.BooleanValue = toggle.IsChecked == true;
                    await current.SubmitAsync();
                };
                editor = toggle;
                break;
            case "Slider":
                var slider = new Slider { Width = UiMetrics.EditorWidth - 44, Minimum = model.Minimum, Maximum = model.Maximum,
                    TickFrequency = model.Step, IsSnapToTickEnabled = model.Step > 0, VerticalAlignment = VerticalAlignment.Center };
                slider.Bind(Slider.ValueProperty, Property(nameof(model.NumberValue), value => value.NumberValue,
                    (value, number) => value.NumberValue = number, BindingMode.TwoWay));
                UserInputCommit.Slider(slider, () => _model?.SubmitAsync() ?? Task.CompletedTask);
                if (model.Id is "bass" or "prompt-volume") slider.TickPlacement = TickPlacement.BottomRight;
                Avalonia.Automation.AutomationProperties.SetName(slider, model.Label);
                // Every slider shows its current step next to the track.
                var value = new TextBlock { MinWidth = 32, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                if (model.Definition.Value is null) value.Text = "—";
                else value.Bind(TextBlock.TextProperty, Property(nameof(model.NumberValue), item => item.NumberValue, format: "{0:0}"));
                _boundEditors.Add(value);
                editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = UiMetrics.SpacingMedium, Children = { slider, value } };
                _boundEditors.Add(slider);
                break;
            case "Radio":
                var radios = new StackPanel { Spacing = 6, MaxWidth = UiMetrics.EditorWidth + 50 };
                var group = "feature-" + model.Id + "-" + Guid.NewGuid().ToString("N");
                foreach (var option in model.Options)
                {
                    var radio = new RadioButton { Content = option.Name, GroupName = group };
                    radio.Bind(RadioButton.IsCheckedProperty, new MultiBinding { Converter = new OptionSelectedConverter(), Bindings =
                        { Property(nameof(model.SelectedOption), value => value.SelectedOption), new CompiledBindingExtension { Source = option } } });
                    radio.Click += async (_, _) =>
                    {
                        if (_model is not { } current) return;
                        current.SelectedOption = option;
                        await current.SubmitAsync();
                    };
                    radios.Children.Add(radio);
                    _boundEditors.Add(radio);
                }
                editor = radios;
                break;
            case "Text":
                var text = new TextBox { Width = UiMetrics.EditorWidth };
                text.Bind(TextBox.TextProperty, Property(nameof(model.TextValue), value => value.TextValue,
                    (value, content) => value.TextValue = content, BindingMode.TwoWay));
                var textDirty = false;
                text.TextInput += (_, _) => textDirty = true;
                text.PastingFromClipboard += (_, _) => textDirty = true;
                text.CuttingToClipboard += (_, _) => textDirty = true;
                text.KeyDown += (_, e) => { if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete) textDirty = true; };
                text.KeyUp += async (_, e) =>
                {
                    if (e.Key != Avalonia.Input.Key.Enter || !textDirty || _model is not { } current) return;
                    textDirty = false;
                    await current.SubmitAsync();
                };
                text.LostFocus += async (_, _) =>
                {
                    if (!text.IsEnabled || !textDirty || _model is not { } current) return;
                    textDirty = false;
                    await current.SubmitAsync();
                };
                editor = text;
                break;
            case "Combo":
                var combo = new ComboBox { ItemsSource = model.Options, MinWidth = 150, MaxWidth = UiMetrics.EditorWidth + 30,
                    ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<UiOption>((option, _) => new TextBlock { Text = option?.Name }) };
                combo.Bind(SelectingItemsControl.SelectedItemProperty, Property(nameof(model.SelectedOption), value => value.SelectedOption,
                    (value, option) => value.SelectedOption = option, BindingMode.TwoWay));
                UserInputCommit.Combo(combo, () => _model?.SubmitAsync() ?? Task.CompletedTask);
                editor = combo;
                break;
            default:
                return false;
        }
        if (!_boundEditors.Contains(editor)) _boundEditors.Add(editor);
        foreach (var bound in _boundEditors) bound.DataContext = model;
        editor.Bind(IsEnabledProperty, Property(nameof(model.CanEdit), value => value.CanEdit));
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        Footer = editor;
        return true;
    }

    private static CompiledBindingExtension Property<T>(string name, Func<FeatureControlViewModel, T> getter,
        Action<FeatureControlViewModel, T>? setter = null, BindingMode mode = BindingMode.OneWay, string? format = null)
    {
        var info = new ClrPropertyInfo(name, target => getter((FeatureControlViewModel)target),
            setter is null ? null : (target, value) => setter((FeatureControlViewModel)target, (T)value!), typeof(T));
        return new CompiledBindingExtension(new CompiledBindingPathBuilder()
            .Property(info, PropertyInfoAccessorFactory.CreateInpcPropertyAccessor).Build())
        {
            Mode = mode, StringFormat = format, DataType = typeof(FeatureControlViewModel)
        };
    }

    private void Hide()
    {
        _boundEditors.Clear();
        Content = null; Description = null; Footer = null;
        IsVisible = false;
    }
}
