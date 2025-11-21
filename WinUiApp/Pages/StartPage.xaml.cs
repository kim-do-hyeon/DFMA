using System;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WinUiApp.Pages.ArtifactsAnalysis;
using WinUiApp.Pages.CaseAnalysis;

namespace WinUiApp.Pages
{
    public sealed partial class StartPage : Page
    {
        public StartPage()
        {
            InitializeComponent();
        }

        private void CreateCase_Button_Click(object sender, RoutedEventArgs e) 
        {
            var window = App.MainWindowInstance as MainWindow;

            if (window != null)
            {
                window.RootFrameControl.Navigate(typeof(CaseAnalysisPage));
            }
        }

        private async void OpenCase_Button_Click(object sender, RoutedEventArgs e)
        {
            var window = App.MainWindowInstance as MainWindow;

            if (window == null)
            {
                return;
            }

            string? caseRoot = null;

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List
            };

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".dfmadb");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }

            caseRoot = Path.GetDirectoryName(file.Path);

            if (!string.IsNullOrWhiteSpace(caseRoot))
            {
                var dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");
                if (File.Exists(dbPath))
                {
                    window.RootFrameControl.Navigate(typeof(CaseReportPage), caseRoot);
                    return;
                }
            }

            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = "케이스를 로드할 수 없습니다",
                Content = "선택한 경로에서 DFMA-Case.dfmadb 파일을 찾을 수 없습니다. 다시 선택해 주세요.",
                CloseButtonText = "확인"
            };

            await dialog.ShowAsync();
        }
    }
}
