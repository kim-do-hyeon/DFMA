using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinUiApp;
using WinUiApp.Pages.CaseAnalysis;
using WinUiApp.Pages.CaseAnalysis.EvidenceSource;
// EvidenceProcess 쪽은 네임스페이스와 타입 이름이 같아서 별칭으로 사용
using EvidenceProcessPage = WinUiApp.Pages.CaseAnalysis.EvidenceProcess.EvidenceProcess;

namespace WinUiApp.Pages
{
    public sealed partial class CaseAnalysisPage : Page
    {
        // 초기 로드시 어떤 Tag 를 대상으로 페이지를 보여줄지 저장
        // 기본값은 "CreateCasePage" (케이스 생성)
        private string _initialTargetTag = "CreateCasePage";

        public CaseAnalysisPage()
        {
            this.InitializeComponent();

            // 페이지 처음 로드 시 초기 페이지 로드
            this.Loaded += CreateCasePage_Loaded;
        }

        // 다른 페이지(예: CreateCasePage, EvidenceSourcePage 등)에서
        // NavigationParameter 로 "EvidenceSource", "StaticAnalysis",
        // "ArtifactsProcess" 등을 넘겨줄 수 있도록 처리
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is string param && !string.IsNullOrWhiteSpace(param))
            {
                // 예:
                //  - "StaticAnalysis"   → 정적 이미지
                //  - "DynamicAnalysis"  → 동적 디스크
                //  - "RemoteAnalysis"   → 원격 디스크
                //  - "EvidenceSource"   → 증거 소스 선택 화면
                //  - "ArtifactsProcess" → 증거 프로세싱 > 아티팩트 프로세싱
                _initialTargetTag = param;
            }
        }

        // 페이지 처음 로드될 때 초기 페이지 선택/표시
        private void CreateCasePage_Loaded(object sender, RoutedEventArgs e)
        {
            NavigationViewItem? targetItem = null;

            // NavigationView 트리 전체에서 Tag 로 항목 검색
            foreach (var item in nvSample.MenuItems.OfType<NavigationViewItem>())
            {
                targetItem = FindNavigationViewItemByTagRecursive(item, _initialTargetTag);
                if (targetItem != null)
                    break;
            }

            // 네비게이션 뷰에서 선택 상태 맞춰주기 (없으면 선택 안 해도 됨)
            if (targetItem != null)
            {
                nvSample.SelectedItem = targetItem;
            }

            // Tag 값에 따라 초기 컨텐츠 프레임 로드
            switch (_initialTargetTag)
            {
                // 케이스 생성
                case "CreateCasePage":
                    contentFrame.Navigate(typeof(CreateCasePage));
                    break;

                // 증거 소스 진입 화면 (EvidenceSourcePage 를 쓰는 경우)
                case "EvidenceSource":
                    contentFrame.Navigate(typeof(EvidenceSourcePage));
                    break;

                // 증거 소스 - 정적 / 동적 / 원격
                case "StaticAnalysis":
                    contentFrame.Navigate(typeof(StaticImage));
                    break;

                case "DynamicAnalysis":
                    contentFrame.Navigate(typeof(DynamicDisk));
                    break;

                case "RemoteAnalysis":
                    contentFrame.Navigate(typeof(RemoteDisk));
                    break;

                // 아티팩트 프로세싱
                case "ArtifactsProcess":
                    contentFrame.Navigate(typeof(EvidenceProcessPage));
                    break;

                default:
                    contentFrame.Content = null;
                    break;
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

            switch (tag)
            {
                // 증거 케이스
                case "CreateCasePage":
                    contentFrame.Navigate(typeof(CreateCasePage));
                    break;

                // 증거 소스 (루트에 Tag="EvidenceSource" 를 달아두었지만
                // SelectsOnInvoked="False" 이므로 실제 선택되는 것은
                // 정적/동적/원격 디스크 같은 하위 항목들뿐)
                case "EvidenceSource":
                    contentFrame.Navigate(typeof(EvidenceSourcePage));
                    break;

                // 증거 소스 - 하위 항목
                case "StaticAnalysis":
                    contentFrame.Navigate(typeof(StaticImage));
                    break;

                case "DynamicAnalysis":
                    contentFrame.Navigate(typeof(DynamicDisk));
                    break;

                case "RemoteAnalysis":
                    contentFrame.Navigate(typeof(RemoteDisk));
                    break;

                // 아티팩트 프로세싱
                case "ArtifactsProcess":
                    contentFrame.Navigate(typeof(EvidenceProcessPage));
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
            if (App.MainWindowInstance is MainWindow window)
            {
                window.RootFrameControl.Navigate(typeof(StartPage));
            }
        }
    }
}
