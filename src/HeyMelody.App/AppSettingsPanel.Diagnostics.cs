using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

public partial class AppSettingsPanel
{
    private readonly Dictionary<string, CheckBox> _logComponents = [];
    private bool _loadingDiagnostics, _diagnosticSelectionDirty, _diagnosticSettingsAvailable;
    private DiagnosticArmIntent _diagnosticArmIntent;
    internal Action<string>? OpenLogDirectoryOverride { get; set; }

    private void InitializeDiagnosticSettings()
    {
        _loadingDiagnostics = true;
        _logComponents.Add(DiagnosticComponents.Main, LogMainCheck);
        _logComponents.Add(DiagnosticComponents.Host, LogHostCheck);
        _logComponents.Add(DiagnosticComponents.DeviceManager, LogManagerCheck);
        _logComponents.Add(DiagnosticComponents.FitTest, LogFitCheck);
        _logComponents.Add(DiagnosticComponents.HearingSetup, LogHearingCheck);
        try
        {
            var settings = DiagnosticSettingsStore.Load();
            foreach (var (component, check) in _logComponents) check.IsChecked = settings.SelectedComponents.Contains(component);
            LogNextStartSwitch.IsChecked = settings.Pending is not null;
            _diagnosticSettingsAvailable = true;
        }
        catch (Exception error) { ShowDiagnosticSettingsError("无法读取日志设置", error.Message); }
        foreach (var check in _logComponents.Values)
        {
            check.IsEnabled = _diagnosticSettingsAvailable;
            check.PropertyChanged += (_, change) =>
            {
                if (_loadingDiagnostics || change.Property != ToggleButton.IsCheckedProperty) return;
                _diagnosticSelectionDirty = true;
                if (!SelectedLogComponents.Any()) LogNextStartSwitch.IsChecked = false;
                PresentDiagnosticSettings();
            };
        }
        LogNextStartSwitch.PropertyChanged += (_, change) =>
        {
            if (_loadingDiagnostics || change.Property != ToggleButton.IsCheckedProperty) return;
            _diagnosticArmIntent = LogNextStartSwitch.IsChecked == true ? DiagnosticArmIntent.ArmNew : DiagnosticArmIntent.CancelPending;
            PresentDiagnosticSettings();
        };
        OpenLogDirectoryButton.Click += (_, _) =>
        {
            try { DiagnosticPaths.OpenLogDirectory(OpenLogDirectoryOverride); }
            catch (Exception error) { ShowDiagnosticSettingsError("打开日志目录失败", error.Message); }
        };
        _loadingDiagnostics = false;
        PresentDiagnosticSettings();
    }

    internal string[] SelectedLogComponents => _logComponents.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray();

    internal bool TrySaveDiagnosticSettings(out string? error)
    {
        error = null;
        // Do not write a dialog's stale arm snapshot after the Host has already consumed it.
        if (!_diagnosticSelectionDirty && _diagnosticArmIntent == DiagnosticArmIntent.Preserve) return true;
        try
        {
            DiagnosticSettingsStore.Save(SelectedLogComponents, _diagnosticArmIntent);
            DiagnosticLog.Info("Settings", "logging.scheduled", "Diagnostic preferences saved",
                new { selectedComponents = SelectedLogComponents, armIntent = _diagnosticArmIntent.ToString() });
            _diagnosticSelectionDirty = false;
            _diagnosticArmIntent = DiagnosticArmIntent.Preserve;
            return true;
        }
        catch (Exception failure) { error = failure.Message; return false; }
    }

    private void PresentDiagnosticSettings()
    {
        LogNextStartSwitch.IsEnabled = _diagnosticSettingsAvailable && SelectedLogComponents.Length != 0;
        var current = DiagnosticRuntime.ReadCurrentSession();
        var currentText = current is { Enabled: true }
            ? "当前后台：本轮正在记录所选组件；退出托盘后台后结束。"
            : "当前后台：未记录。";
        var next = LogNextStartSwitch.IsChecked == true
            ? _diagnosticArmIntent == DiagnosticArmIntent.ArmNew ? "保存后安排下一次后台启动记录。" : "已安排下一次后台启动记录。"
            : "下一次后台启动不记录。";
        LogStatusText.Text = currentText + Environment.NewLine + next;
        LogDirectoryText.Text = DiagnosticPaths.LogRoot;
    }

    private void ShowDiagnosticSettingsError(string title, string message)
    {
        LogSettingsNotice.Title = title;
        LogSettingsNotice.Message = message;
        LogSettingsNotice.IsOpen = LogSettingsNotice.IsVisible = true;
    }
}
