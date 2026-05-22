// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Toolbox;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public static Window? MainWindow { get; private set; }

    public static IntPtr GetMainWindowHandle()
    {
        if (MainWindow is null)
        {
            throw new InvalidOperationException("Main window has not been created.");
        }

        return WindowNative.GetWindowHandle(MainWindow);
    }

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }
}
