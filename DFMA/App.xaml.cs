using Microsoft.UI.Xaml;
using Microsoft.Win32;
using System;

namespace WinUiApp
{
    public partial class App : Application
    {
        public static Window? MainWindowInstance { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            MainWindowInstance = new MainWindow();
            MainWindowInstance.Activate();
        }
    }
}