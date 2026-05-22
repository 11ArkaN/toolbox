// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Toolbox.Pages;

namespace Toolbox;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Extend content into the title bar and use the TabView's
        // drag region so the tab strip acts as the title bar area.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomDragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Reserve space at the right edge of the tab strip for the system
        // caption buttons, scaled to the current DPI so they don't overlap
        // tabs on high-DPI displays.
        AppWindow.Changed += OnAppWindowChanged;
        UpdateCaptionButtonInset();

        AddTab("Audio Splitter", typeof(AudioSplitterPage), "\uE768");
        AddTab("Slide Splitter", typeof(SlideSplitterPage), "\uE8A5");
        TabControl.SelectedIndex = 0;
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPositionChange)
        {
            UpdateCaptionButtonInset();
        }
    }

    private void UpdateCaptionButtonInset()
    {
        // RightInset is in physical pixels; convert to DIPs.
        double scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }

        CustomDragRegion.MinWidth = AppWindow.TitleBar.RightInset / scale;
    }

    private TabViewItem AddTab(string header, System.Type pageType, string glyph)
    {
        var tab = new TabViewItem
        {
            Header = header,
            IconSource = new FontIconSource
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons")
            },
            IsClosable = false
        };

        var frame = new Frame();
        frame.Navigate(pageType);
        tab.Content = frame;

        TabControl.TabItems.Add(tab);
        return tab;
    }
}
