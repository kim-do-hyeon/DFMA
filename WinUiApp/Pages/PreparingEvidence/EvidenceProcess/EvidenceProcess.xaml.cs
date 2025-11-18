using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using WinUiApp.Interop;
using WinUiApp.Pages;
using WinUiApp.Pages.CaseAnalysis;

namespace WinUiApp.Pages.CaseAnalysis.EvidenceProcess
{
    public sealed partial class EvidenceProcess : Page
    {
        //  페이지 간 이동 후에도 유지할 static 상태
        private static bool? _artifactAllSaved;
        private static bool? _browserAllSaved;
        private static bool? _commAllSaved;
        private static bool? _timelineAllSaved;

        private static bool?[] _artifactSaved = new bool?[10]; // 아티팩트 10개
        private static bool?[] _browserSaved = new bool?[5];  // 브라우저 5개
        private static bool?[] _commSaved = new bool?[3];  // 커뮤니케이션 3개
        private static bool?[] _timelineSaved = new bool?[1];  // 타임라인 1개

        private bool _isLoading = false;

        public EvidenceProcess()
        {
            this.InitializeComponent();

            _isLoading = true;
            LoadSavedState();
            _isLoading = false;
        }

        //  공용 함수
        private void SetChildren(CheckBox[] children, bool? value)
        {
            foreach (var c in children)
                c.IsChecked = value;
        }

        private void UpdateParent(CheckBox parent, CheckBox[] children)
        {
            int checkedCount = children.Count(c => c.IsChecked == true);
            int uncheckedCount = children.Count(c => c.IsChecked == false);

            if (checkedCount == children.Length)
                parent.IsChecked = true;
            else if (uncheckedCount == children.Length)
                parent.IsChecked = false;
            else
                parent.IsChecked = null; // Indeterminate
        }

        //  체크박스 배열
        private CheckBox[] ArtifactChildren => new[]
        {
            Artifact_EventLog, Artifact_Prefetch, Artifact_JumpList,
            Artifact_LinkFile, Artifact_PowerShellHistory, Artifact_NotificationCenter,
            Artifact_MRU, Artifact_ShellBag, Artifact_SRUM, Artifact_AmCache
        };

        private CheckBox[] BrowserChildren => new[]
        {
            Browser_Search, Browser_Visit, Browser_Download,
            Browser_Cache, Browser_Cookie
        };

        private CheckBox[] CommChildren => new[]
        {
            Comm_Email, Comm_Message, Comm_Cloud
        };

        private CheckBox[] TimelineChildren => new[]
        {
            Timeline_Main
        };

        //  상태 저장 / 불러오기
        private void SaveState()
        {
            if (_isLoading) return;

            _artifactAllSaved = ArtifactAllCheckBox?.IsChecked;
            _browserAllSaved = BrowserAllCheckBox?.IsChecked;
            _commAllSaved = CommAllCheckBox?.IsChecked;
            _timelineAllSaved = TimelineAllCheckBox?.IsChecked;

            for (int i = 0; i < ArtifactChildren.Length; i++)
                _artifactSaved[i] = ArtifactChildren[i].IsChecked;

            for (int i = 0; i < BrowserChildren.Length; i++)
                _browserSaved[i] = BrowserChildren[i].IsChecked;

            for (int i = 0; i < CommChildren.Length; i++)
                _commSaved[i] = CommChildren[i].IsChecked;

            for (int i = 0; i < TimelineChildren.Length; i++)
                _timelineSaved[i] = TimelineChildren[i].IsChecked;
        }

        private void LoadSavedState()
        {
            if (_artifactAllSaved.HasValue)
                ArtifactAllCheckBox.IsChecked = _artifactAllSaved;
            if (_browserAllSaved.HasValue)
                BrowserAllCheckBox.IsChecked = _browserAllSaved;
            if (_commAllSaved.HasValue)
                CommAllCheckBox.IsChecked = _commAllSaved;
            if (_timelineAllSaved.HasValue)
                TimelineAllCheckBox.IsChecked = _timelineAllSaved;

            for (int i = 0; i < ArtifactChildren.Length; i++)
                if (_artifactSaved[i].HasValue)
                    ArtifactChildren[i].IsChecked = _artifactSaved[i];

            for (int i = 0; i < BrowserChildren.Length; i++)
                if (_browserSaved[i].HasValue)
                    BrowserChildren[i].IsChecked = _browserSaved[i];

            for (int i = 0; i < CommChildren.Length; i++)
                if (_commSaved[i].HasValue)
                    CommChildren[i].IsChecked = _commSaved[i];

            for (int i = 0; i < TimelineChildren.Length; i++)
                if (_timelineSaved[i].HasValue)
                    TimelineChildren[i].IsChecked = _timelineSaved[i];
        }

        //  아티팩트 분석
        private void SelectAllArtifact_Checked(object sender, RoutedEventArgs e)
        {
            SetChildren(ArtifactChildren, true);
            SaveState();
        }

        private void SelectAllArtifact_Unchecked(object sender, RoutedEventArgs e)
        {
            SetChildren(ArtifactChildren, false);
            SaveState();
        }

        private void SelectAllArtifact_Indeterminate(object sender, RoutedEventArgs e) { }

        private void ArtifactOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateParent(ArtifactAllCheckBox, ArtifactChildren);
            SaveState();
        }

        private void ArtifactOption_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateParent(ArtifactAllCheckBox, ArtifactChildren);
            SaveState();
        }

        //  브라우저 분석
        private void SelectAllBrowser_Checked(object sender, RoutedEventArgs e)
        {
            SetChildren(BrowserChildren, true);
            SaveState();
        }

        private void SelectAllBrowser_Unchecked(object sender, RoutedEventArgs e)
        {
            SetChildren(BrowserChildren, false);
            SaveState();
        }

        private void SelectAllBrowser_Indeterminate(object sender, RoutedEventArgs e) { }

        private void BrowserOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateParent(BrowserAllCheckBox, BrowserChildren);
            SaveState();
        }

        private void BrowserOption_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateParent(BrowserAllCheckBox, BrowserChildren);
            SaveState();
        }

        //  커뮤니케이션 분석
        private void SelectAllComm_Checked(object sender, RoutedEventArgs e)
        {
            SetChildren(CommChildren, true);
            SaveState();
        }

        private void SelectAllComm_Unchecked(object sender, RoutedEventArgs e)
        {
            SetChildren(CommChildren, false);
            SaveState();
        }

        private void SelectAllComm_Indeterminate(object sender, RoutedEventArgs e) { }

        private void CommOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateParent(CommAllCheckBox, CommChildren);
            SaveState();
        }

        private void CommOption_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateParent(CommAllCheckBox, CommChildren);
            SaveState();
        }

        //  타임라인 분석
        private void SelectAllTimeline_Checked(object sender, RoutedEventArgs e)
        {
            SetChildren(TimelineChildren, true);
            SaveState();
        }

        private void SelectAllTimeline_Unchecked(object sender, RoutedEventArgs e)
        {
            SetChildren(TimelineChildren, false);
            SaveState();
        }

        private void SelectAllTimeline_Indeterminate(object sender, RoutedEventArgs e) { }

        private void TimelineOption_Checked(object sender, RoutedEventArgs e)
        {
            UpdateParent(TimelineAllCheckBox, TimelineChildren);
            SaveState();
        }

        private void TimelineOption_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateParent(TimelineAllCheckBox, TimelineChildren);
            SaveState();
        }

        //  "증거 분석" 버튼 클릭 → DB 저장
        private async void EvidenceAnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            // 현재 체크 상태 static에 반영
            SaveState();

            // 1) 현재 케이스 폴더 절대 경로 가져오기
            var caseRoot = CreateCasePage.CurrentCaseRoot;

            if (string.IsNullOrEmpty(caseRoot))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "케이스 정보 없음",
                    Content = "케이스 폴더 정보를 찾을 수 없습니다.\n먼저 케이스를 생성한 후 다시 시도해 주세요.",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            // 2) 실제 케이스 DB 경로 (예: C:\...\DFMA-Case.dfmadb)
            string dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");

            if (!File.Exists(dbPath))
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "DB 없음",
                    Content = $"케이스 DB 파일을 찾을 수 없습니다.\n경로: {dbPath}",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            // 3) SQLite 열기
            IntPtr db;
            int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
            int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "DB 열기 실패",
                    Content = $"데이터베이스를 열 수 없습니다.\n경로: {dbPath}\nrc={rc}",
                    CloseButtonText = "확인"
                }.ShowAsync();
                return;
            }

            try
            {
                // 테이블 없으면 생성
                string createTableSql =
                    "CREATE TABLE IF NOT EXISTS artifacts_process (" +
                    " key   TEXT PRIMARY KEY," +
                    " value INTEGER NOT NULL" +
                    ");";

                NativeSqliteHelper.ExecNonQuery(db, createTableSql);

                // 0 : 아티팩트 프로세싱 안함
                // 1 : 아티팩트 프로세싱 함
                // 2 : 아티팩트 프로세싱 중
                // 3 : 아티팩트 프로세싱 완료
                int State(bool? b) => (b == true) ? 1 : 0;

                var artifacts = new Dictionary<string, int>
        {
            // 아티팩트 분석
            { "Artifact.EventLogAnalysis",          State(Artifact_EventLog.IsChecked) },
            { "Artifact.PrefetchAnalysis",          State(Artifact_Prefetch.IsChecked) },
            { "Artifact.JumpListAnalysis",          State(Artifact_JumpList.IsChecked) },
            { "Artifact.LinkFileAnalysis",          State(Artifact_LinkFile.IsChecked) },
            { "Artifact.PowerShellHistoryAnalysis", State(Artifact_PowerShellHistory.IsChecked) },
            { "Artifact.NotificationCenterAnalysis",State(Artifact_NotificationCenter.IsChecked) },
            { "Artifact.MRUAnalysis",               State(Artifact_MRU.IsChecked) },
            { "Artifact.ShellBagAnalysis",          State(Artifact_ShellBag.IsChecked) },
            { "Artifact.SrumAnalysis",              State(Artifact_SRUM.IsChecked) },
            { "Artifact.AmCacheAnalysis",           State(Artifact_AmCache.IsChecked) },

            // 브라우저 분석
            { "Browser.SearchAnalysis",             State(Browser_Search.IsChecked) },
            { "Browser.VisitAnalysis",              State(Browser_Visit.IsChecked) },
            { "Browser.DownloadAnalysis",           State(Browser_Download.IsChecked) },
            { "Browser.CacheAnalysis",              State(Browser_Cache.IsChecked) },
            { "Browser.CookieAnalysis",             State(Browser_Cookie.IsChecked) },

            // 커뮤니케이션 분석
            { "Communication.MailAnalysis",         State(Comm_Email.IsChecked) },
            { "Communication.MessageAnalysis",      State(Comm_Message.IsChecked) },
            { "Communication.CloudAnalysis",        State(Comm_Cloud.IsChecked) },

            // 타임라인 분석
            { "TimelineAnalysis.Main",              State(Timeline_Main.IsChecked) },
        };

                foreach (var kv in artifacts)
                {
                    string key = kv.Key.Replace("'", "''");
                    // 0 : 아티팩트 프로세싱 안함
                    // 1 : 아티팩트 프로세싱 함
                    // 2 : 아티팩트 프로세싱 중
                    // 3 : 아티팩트 프로세싱 완료
                    int value = kv.Value;

                    string sql =
                        $"INSERT OR REPLACE INTO artifacts_process(key, value) " +
                        $"VALUES('{key}', {value});";

                    NativeSqliteHelper.ExecNonQuery(db, sql);
                }

                // 증거 분석에서 케이스 열기
                if (App.MainWindowInstance is MainWindow window)
                {
                    window.RootFrameControl.Navigate(typeof(ArtifactsAnalysisPage), caseRoot);
                }
            }
            catch (Exception ex)
            {
                await new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "저장 오류",
                    Content = $"저장 중 오류가 발생했습니다.\n{ex.Message}",
                    CloseButtonText = "확인"
                }.ShowAsync();
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }
        }
    }
}
