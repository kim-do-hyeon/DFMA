using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

using WinUiApp;

namespace WinUiApp.Pages.CaseAnalysis
{
    public sealed partial class EvidenceSourcePage : Page
    {
        public EvidenceSourcePage()
        {
            this.InitializeComponent();
        }

        // 정적 이미지 버튼 클릭 -> NavigationView 의 "정적 이미지" 선택과 동일하게 동작
        private void StaticImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance is MainWindow window)
            {
                // CaseAnalysisPage 를 "StaticAnalysis" Tag 로 다시 열기
                window.RootFrameControl.Navigate(typeof(WinUiApp.Pages.CaseAnalysisPage), "StaticAnalysis");
            }
        }

        // 동적 디스크 버튼 클릭
        private void DynamicDiskButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance is MainWindow window)
            {
                window.RootFrameControl.Navigate(typeof(WinUiApp.Pages.CaseAnalysisPage), "DynamicAnalysis");
            }
        }

        // 원격 디스크 버튼 클릭
        private void RemoteDiskButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance is MainWindow window)
            {
                window.RootFrameControl.Navigate(typeof(WinUiApp.Pages.CaseAnalysisPage), "RemoteAnalysis");
            }
        }
    }
}
