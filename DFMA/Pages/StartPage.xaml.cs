using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUiApp.Pages
{
    public sealed partial class StartPage : Page
    {
        public StartPage()
        {
            InitializeComponent();
        }

        private void CreateCase_Button_Click(object sender, RoutedEventArgs e)  // 케이스 생성 페이지 로드
        {
            var window = App.MainWindowInstance as MainWindow;

            if (window != null)
            {
                window.RootFrameControl.Navigate(typeof(CaseAnalysisPage));
            }
        }

        private void OpenCase_Button_Click(object sender, RoutedEventArgs e)  // 아티팩트 분석 페이지 로드
        {
            var window = App.MainWindowInstance as MainWindow;

            if (window != null)
            {
                window.RootFrameControl.Navigate(typeof(ArtifactsAnalysisPage));
            }
        }
    }
}
