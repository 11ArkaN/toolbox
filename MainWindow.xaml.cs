// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Windowing;
using Microsoft.UI.Composition.SystemBackdrops;
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
        ApplySystemBackdrop();

        // Extend content into the title bar and use a dedicated drag region
        // so the sidebar remains fully interactive.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(CustomDragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Reserve space at the right edge of the tab strip for the system
        // caption buttons, scaled to the current DPI so they don't overlap
        // tabs on high-DPI displays.
        AppWindow.Changed += OnAppWindowChanged;
        UpdateCaptionButtonInset();

        Sidebar.SelectedItem = AudioNavigationItem;
        NavigateToTool("audio");
    }

    private void ApplySystemBackdrop()
    {
        SystemBackdrop = MicaController.IsSupported()
            ? new MicaBackdrop { Kind = MicaKind.BaseAlt }
            : new DesktopAcrylicBackdrop();
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

        CaptionButtonPadding.Width = AppWindow.TitleBar.RightInset / scale;
    }

    private void Sidebar_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateToTool(tag);
        }
    }

    private void NavigateToTool(string tag)
    {
        Type pageType = tag switch
        {
            "audio-mp3" => typeof(AudioConverterPage),
            "slides" => typeof(SlideSplitterPage),
            "rename" => typeof(FileRenamerPage),
            "pdf" => typeof(PdfToolboxPage),
            "docx-watermark" => typeof(DocxWatermarkPage),
            "images" => typeof(ImageConverterPage),
            _ => typeof(AudioSplitterPage)
        };

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
