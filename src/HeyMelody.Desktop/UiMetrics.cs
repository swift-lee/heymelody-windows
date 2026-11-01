namespace HeyMelodyWindows.Ui;

/// <summary>Layout values for UI built in code; they mirror the tokens in Styles/HeyMelodyTheme.axaml.</summary>
internal static class UiMetrics
{
    internal const double Spacing = 8;
    internal const double SpacingMedium = 12;
    internal const double SpacingLarge = 16;
    internal const double PageMargin = 24;
    internal const double CompactPageMargin = 16;
    internal const double FooterHeight = 72;
    internal const double ActivityBarHeight = 3;
    /// <summary>Horizontal inset of expander content; equals FluentAvalonia's SettingsExpanderPadding and item padding (16).</summary>
    internal const double ExpanderContentInset = 16;
    /// <summary>Width of a slider, text box or combo box in a settings row's footer.</summary>
    internal const double EditorWidth = 200;

    /// <summary>Main window breakpoints: below 1080 the panes narrow, at 1600 by 1000 they widen.</summary>
    internal const double CompactWidth = 1080;
    internal const double WideWidth = 1600;
    internal const double WideHeight = 1000;
    /// <summary>
    /// Control pane widths. FluentAvalonia moves a settings row's footer below its label under 460 px, so the pane never
    /// becomes narrower than 480 px.
    /// </summary>
    internal const double PaneWidth = 500;
    internal const double CompactPaneWidth = 480;
    internal const double WidePaneWidth = 540;
    /// <summary>Largest hero artwork in each tier; the 3D view measures to the same limits.</summary>
    internal const double HeroMaxWidth = 600, HeroMaxHeight = 520;
    internal const double CompactHeroMaxHeight = 360;
    internal const double WideHeroMaxWidth = 720, WideHeroMaxHeight = 640;
    internal const double WideContentMaxWidth = 1680;
}
