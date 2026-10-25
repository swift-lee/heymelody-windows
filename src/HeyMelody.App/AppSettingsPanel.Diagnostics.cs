using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

public partial class AppSettingsPanel
{
    private readonly Dictionary<string, CheckBox> _logComponents = [];
    private bool _loadingDiagnostics, _syncingSelectAll, _diagnosticSelectionDirty, _diagnosticSettingsAvailable;
    private DiagnosticArmIntent _diagnosticArmIntent;
    private DiagnosticSessionInfo? _currentDiagnosticSession;
    internal Action<string>? OpenLogDirectoryOverride { get; set; }
    internal Func<DiagnosticSessionInfo?>? CurrentDiagnosticSessionOverride { get; set; }

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
        catch (Exception error)
        {
            DiagnosticLog.Warning("Settings", "logging.loadFailed", "Diagnostic preferences could not be read", error);
            ShowDiagnosticSettingsError("无法读取日志设置", error.Message);
        }
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
        LogSelectAllCheck.IsEnabled = _diagnosticSettingsAvailable;
        // The parent box only mirrors the children (mixed while partly selected); a click selects all or clears all.
        LogSelectAllCheck.PropertyChanged += (_, change) =>
        {
            if (_loadingDiagnostics || _syncingSelectAll || change.Property != ToggleButton.IsCheckedProperty) return;
            if (LogSelectAllCheck.IsChecked is { } selected) SetAllLogComponents(selected);
        };
        LogNextStartSwitch.PropertyChanged += (_, change) =>
        {
            if (_loadingDiagnostics || change.Property != ToggleButton.IsCheckedProperty) return;
            _diagnosticArmIntent = LogNextStartSwitch.IsChecked == true ? DiagnosticArmIntent.ArmNew : DiagnosticArmIntent.CancelPending;
            PresentDiagnosticSettings();
        };
        // The Host cycle may start or end while the dialog is open; re-read it whenever the section is revealed.
        DiagnosticExpander.PropertyChanged += (_, change) =>
        {
            if (change.Property == FASettingsExpander.IsExpandedProperty && DiagnosticExpander.IsExpanded) RefreshDiagnosticSession();
        };
        LogSettingsNotice.Closed += (_, _) => LogSettingsNotice.IsVisible = false;
        OpenLogDirectoryButton.Click += (_, _) => OpenDirectory(null);
        OpenSessionLogButton.Click += (_, _) => OpenDirectory(_currentDiagnosticSession?.Directory);
        _loadingDiagnostics = false;
        RefreshDiagnosticSession();
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
        catch (Exception failure)
        {
            DiagnosticLog.Warning("Settings", "logging.saveFailed", "Diagnostic preferences could not be saved", failure,
                new { selectedComponents = SelectedLogComponents, armIntent = _diagnosticArmIntent.ToString() });
            error = failure.Message;
            return false;
        }
    }

    private void SetAllLogComponents(bool selected)
    {
        foreach (var check in _logComponents.Values) check.IsChecked = selected;
        PresentDiagnosticSettings();
    }

    private void OpenDirectory(string? sessionDirectory)
    {
        try
        {
            if (sessionDirectory is null) DiagnosticPaths.OpenLogDirectory(OpenLogDirectoryOverride);
            else DiagnosticPaths.OpenLogDirectory(sessionDirectory, OpenLogDirectoryOverride);
            HideDiagnosticSettingsError();
        }
        catch (Exception error)
        {
            DiagnosticLog.Warning("Settings", "logging.openDirectoryFailed", "Log directory could not be opened", error,
                new { sessionDirectory });
            ShowDiagnosticSettingsError("打开日志目录失败", error.Message);
        }
    }

    private void RefreshDiagnosticSession()
    {
        try { _currentDiagnosticSession = (CurrentDiagnosticSessionOverride ?? DiagnosticRuntime.ReadCurrentSession)(); }
        catch (Exception error)
        {
            DiagnosticLog.Debug("Settings", "logging.sessionReadFailed", "Current diagnostic session could not be read",
                new { error = error.ToString() });
            _currentDiagnosticSession = null;
        }
        PresentDiagnosticSettings();
    }

    private void PresentDiagnosticSettings()
    {
        var selectedCount = SelectedLogComponents.Length;
        LogNextStartSwitch.IsEnabled = _diagnosticSettingsAvailable && selectedCount != 0;
        LogNoComponentHint.IsVisible = _diagnosticSettingsAvailable && selectedCount == 0;
        _syncingSelectAll = true;
        LogSelectAllCheck.IsChecked = selectedCount == _logComponents.Count ? true : selectedCount == 0 ? false : null;
        _syncingSelectAll = false;
        // Only a running cycle is reported; the switch itself shows whether the next start records.
        var current = _currentDiagnosticSession is { Enabled: true } session ? session : null;
        LogStatusBar.IsOpen = LogStatusBar.IsVisible = current is not null;
        if (current is not null)
            LogStatusBar.Message = $"本轮开始于 {current.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}，"
                + $"记录 {string.Join("、", current.Components)}；从托盘退出后台后结束。";
        OpenSessionLogButton.IsVisible = current?.Directory is not null;
        LogDirectoryItem.Description = DiagnosticPaths.LogRoot;
    }

    private void ShowDiagnosticSettingsError(string title, string message)
    {
        LogSettingsNotice.Title = title;
        LogSettingsNotice.Message = message;
        // A load failure explains the disabled controls, so it cannot be dismissed.
        LogSettingsNotice.IsClosable = _diagnosticSettingsAvailable;
        LogSettingsNotice.IsOpen = LogSettingsNotice.IsVisible = true;
    }

    private void HideDiagnosticSettingsError()
    {
        // A failed load keeps its notice: the controls stay disabled until the dialog is reopened.
        if (!_diagnosticSettingsAvailable) return;
        LogSettingsNotice.IsOpen = LogSettingsNotice.IsVisible = false;
    }
}
