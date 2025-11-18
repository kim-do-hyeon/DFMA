using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.Linq;
using WinUiApp.Pages.ArtifactsAnalysis;
using WinUiApp.Pages.EvidenceAnalysis.FilesystemAnalysis;

namespace WinUiApp.Pages
{
    public sealed partial class ArtifactsAnalysisPage : Page
    {
        // EvidenceProcess → Navigate 시 넘겨주는 케이스 루트 경로
        private string? _caseRootFromParameter;

        public ArtifactsAnalysisPage()
        {
            this.InitializeComponent();

            // 페이지 처음 로드 시 "케이스 정보" 페이지 로드
            this.Loaded += ArtifactsAnalysisPage_Loaded;
        }

        // StartPage / EvidenceProcess 에서 넘어올 때 파라미터 받기
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // EvidenceProcess에서 Navigate(typeof(ArtifactsAnalysisPage), caseRoot) 로 넘겨준 값
            _caseRootFromParameter = e.Parameter as string;
        }

        // CaseImformation 페이지 기본 로드
        private void ArtifactsAnalysisPage_Loaded(object sender, RoutedEventArgs e)
        {
            NavigationViewItem? caseItem = null;

            foreach (var item in nvSample.MenuItems.OfType<NavigationViewItem>())
            {
                caseItem = FindNavigationViewItemByTagRecursive(item, "CaseImformation");
                if (caseItem != null)
                    break;
            }

            if (caseItem != null)
            {
                nvSample.SelectedItem = caseItem;

                // EvidenceProcess에서 왔으면 _caseRootFromParameter 에 케이스 절대 경로가 있고,
                // StartPage → "케이스 열기" 에서 왔으면 null 이라서 CaseImformation 쪽에서
                // 빈 화면 + "케이스 폴더 경로" 찾아보기로만 열리게 된다.
                contentFrame.Navigate(typeof(CaseImformation), _caseRootFromParameter);
            }
        }

        // 네비게이션 내부 페이지 로드
        private void NavigationView_SelectionChanged(
            NavigationView sender,
            NavigationViewSelectionChangedEventArgs args)
        {
            var selectedItem = args.SelectedItemContainer as NavigationViewItem;
            if (selectedItem?.Tag is not string tag)
            {
                return;
            }

            // Tag 별 페이지 로드
            switch (tag)
            {
                case "CaseImformation":
                    // 현재 케이스 경로 파라미터를 항상 함께 넘긴다.
                    contentFrame.Navigate(typeof(CaseImformation), _caseRootFromParameter);
                    break;

                case "FilesystemAnalysis":
                    // 파일 시스템 분석 탭 선택 시 파일 시스템 페이지로 이동
                    contentFrame.Navigate(typeof(FilesystemAnalysis), _caseRootFromParameter);
                    break;

                default:
                    contentFrame.Content = null;
                    break;
            }
        }

        // 네비게이션 트리 내부 Tag 서치 메서드
        private NavigationViewItem? FindNavigationViewItemByTagRecursive(
            NavigationViewItem parent,
            string tag)
        {
            if (parent.Tag is string current && current == tag)
                return parent;

            foreach (var child in parent.MenuItems.OfType<NavigationViewItem>())
            {
                var found = FindNavigationViewItemByTagRecursive(child, tag);
                if (found != null)
                    return found;
            }

            return null;
        }

        // 뒤로가기 버튼 호출
        private void NavigationView_BackRequested(
            NavigationView sender,
            NavigationViewBackRequestedEventArgs args)
        {
            var window = App.MainWindowInstance as MainWindow;

            if (window != null)
            {
                window.RootFrameControl.Navigate(typeof(StartPage));
            }
        }
    }
}
