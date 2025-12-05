using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using System;
using System.IO;
using System.Text;
using System.Text.Json;

using Windows.ApplicationModel;
using Windows.Storage.Pickers;
using WinRT.Interop;

using WinUiApp.Interop;
using WinUiApp.Services;
using WinUiApp.Pages.ArtifactsAnalysis;

namespace WinUiApp.Pages.CaseAnalysis
{
    public sealed partial class CreateCasePage : Page
    {
        // 페이지를 변경해도 정보 유지용 static 필드
        private static string? _savedCaseName;
        private static string? _savedCaseFolderName;
        private static string? _savedCaseFolderPath;

        // 현재 케이스 폴더의 절대 경로
        internal static string? CurrentCaseRoot
        {
            get
            {
                if (string.IsNullOrEmpty(_savedCaseFolderPath) ||
                    string.IsNullOrEmpty(_savedCaseFolderName))
                    return null;

                return Path.Combine(_savedCaseFolderPath, _savedCaseFolderName);
            }
        }

        public CreateCasePage()
        {
            this.InitializeComponent();
            LoadStateOrDefaults();
            HookTextChangedForState();
        }

        // 저장된 상태가 있으면 불러오고, 없으면 기본값 적용
        private void LoadStateOrDefaults()
        {
            if (_savedCaseName is null &&
                _savedCaseFolderName is null &&
                _savedCaseFolderPath is null)
            {
                // 최초 진입: 기본값 설정
                CaseNameTextBox.Text = string.Empty;

                var now = DateTime.Now;
                CaseFolderNameTextBox.Text = $"DFMA {now:yyyy-MM-dd HHmmss}";

                CaseFolderPathTextBox.Text = @"C:\";

                SaveCurrentToStatic();
            }
            else
            {
                // 저장된 값 복원
                CaseNameTextBox.Text = _savedCaseName ?? string.Empty;
                CaseFolderNameTextBox.Text = _savedCaseFolderName ?? string.Empty;
                CaseFolderPathTextBox.Text = _savedCaseFolderPath ?? string.Empty;
            }
        }

        // TextBox 내용이 바뀔 때마다 static 상태 업데이트
        private void HookTextChangedForState()
        {
            CaseNameTextBox.TextChanged += (_, __) => SaveCurrentToStatic();
            CaseFolderNameTextBox.TextChanged += (_, __) => SaveCurrentToStatic();
            CaseFolderPathTextBox.TextChanged += (_, __) => SaveCurrentToStatic();
        }

        private void SaveCurrentToStatic()
        {
            _savedCaseName = CaseNameTextBox.Text;
            _savedCaseFolderName = CaseFolderNameTextBox.Text;
            _savedCaseFolderPath = CaseFolderPathTextBox.Text;
        }

        // 폴더 찾아보기 버튼 클릭
        private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add("*");

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                CaseFolderPathTextBox.Text = folder.Path;
                SaveCurrentToStatic();
            }
        }

        // "케이스 생성" 버튼 클릭
        // 1) 값 검증
        // 2) 케이스 폴더 + DB 파일 생성
        // 3) Microsoft.Data.Sqlite로 로컬 DB 초기화
        // 4) 성공 시 CaseAnalysisPage 로 이동
        private async void CreateCase_Button_Click(object sender, RoutedEventArgs e)
        {
            var caseName = CaseNameTextBox.Text?.Trim();
            var folderName = CaseFolderNameTextBox.Text?.Trim();
            var folderPath = CaseFolderPathTextBox.Text?.Trim();

            // 필수 항목 확인
            if (string.IsNullOrEmpty(caseName) ||
                string.IsNullOrEmpty(folderName) ||
                string.IsNullOrEmpty(folderPath))
            {
                // 로그: 입력 누락
                AnalysisLogHelper.Warn(
                    category: "CaseCreate",
                    message: "케이스 생성 실패 - 필수 항목 누락",
                    data: new
                    {
                        case_name = caseName,
                        folder_name = folderName,
                        folder_path = folderPath
                    });

                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "필수 항목 누락",
                    Content = "모든 필수 항목을 입력해 주세요.",
                    CloseButtonText = "확인"
                };
                await dialog.ShowAsync();
                return;
            }

            string caseRoot = Path.Combine(folderPath, folderName);
            string dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");

            // 이미 케이스 존재하면 재생성 불가
            if (File.Exists(dbPath))
            {
                // 로그: 이미 존재하는 케이스
                AnalysisLogHelper.Warn(
                    category: "CaseCreate",
                    message: "케이스 생성 스킵 - 이미 존재",
                    data: new
                    {
                        case_name = caseName,
                        case_root = caseRoot,
                        db_path = dbPath
                    });

                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "이미 존재하는 케이스",
                    Content = $"이미 생성된 케이스가 있습니다.\n경로: {dbPath}",
                    CloseButtonText = "확인"
                };
                await dialog.ShowAsync();
                return;
            }

            try
            {
                Directory.CreateDirectory(caseRoot);

                // 3) DB 생성 및 초기화 (테이블 + case_info row 삽입)
                // Microsoft.Data.Sqlite가 자동으로 네이티브 DLL을 관리합니다.
                CreateAndInitDatabase(dbPath, caseName!);

                // 로그: 케이스 생성 성공
                AnalysisLogHelper.Write(
                    caseRoot,
                    level: "INFO",
                    category: "CaseCreate",
                    message: "케이스 생성 성공",
                    data: new
                    {
                        case_name = caseName,
                        case_root = caseRoot,
                        db_path = dbPath
                    });
            }
            catch (Exception ex)
            {
                // 로그: 케이스 생성 실패
                AnalysisLogHelper.Write(
                    caseRoot,
                    level: "ERROR",
                    category: "CaseCreate",
                    message: "케이스 생성 실패",
                    data: new
                    {
                        case_name = caseName,
                        case_root = caseRoot,
                        db_path = dbPath,
                        exception_type = ex.GetType().FullName,
                        exception_message = ex.Message
                    });

                var dialog = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    Title = "케이스 생성 실패",
                    Content = $"케이스를 생성하는 중 오류가 발생했습니다.\n\n{ex.Message}",
                    CloseButtonText = "확인"
                };
                await dialog.ShowAsync();
                return;
            }

            // 값/DB 생성까지 모두 성공했으면 페이지 이동
            if (App.MainWindowInstance is MainWindow window2)
            {
                window2.RootFrameControl.Navigate(
                    typeof(WinUiApp.Pages.CaseAnalysisPage),
                    "EvidenceSource");
            }
        }

        // DFMA-Case.dfmadb 파일 생성 및 스키마/초기 데이터 작성
        private static void CreateAndInitDatabase(string dbPath, string caseName)
        {
            // SQLite DB 열기 (없으면 생성)
            IntPtr db;
            int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE |
                        NativeSqliteHelper.SQLITE_OPEN_CREATE;

            int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);
            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                if (db != IntPtr.Zero)
                {
                    NativeSqliteHelper.sqlite3_close(db);
                }
                throw new InvalidOperationException($"SQLite 데이터베이스를 열 수 없습니다. (rc={rc})");
            }

            try
            {
                const string createCaseInfoTable = @"
                    CREATE TABLE IF NOT EXISTS case_info
                    (
                        key   TEXT NOT NULL PRIMARY KEY,
                        value TEXT
                    );";

                const string createArtifactsTable = @"
                    CREATE TABLE IF NOT EXISTS artifacts_eventlog
                    (
                        id             INTEGER PRIMARY KEY AUTOINCREMENT,
                        event_time_utc TEXT,
                        level          TEXT,
                        source         TEXT,
                        message        TEXT
                    );";

                NativeSqliteHelper.ExecNonQuery(db, createCaseInfoTable);
                NativeSqliteHelper.ExecNonQuery(db, createArtifactsTable);

                // 시간은 무조건 UTC+00:00
                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
                string timezone = "UTC+00:00";

                string dbVersion = "1.0";
                string toolVersion = GetToolVersion();
                string caseUuid = Guid.NewGuid().ToString();

                NativeSqliteHelper.InsertKeyValue(db, "CaseName", caseName);
                NativeSqliteHelper.InsertKeyValue(db, "CaseCreateTime", utcNow);
                NativeSqliteHelper.InsertKeyValue(db, "Timezone", timezone);
                NativeSqliteHelper.InsertKeyValue(db, "DatabaseVersion", dbVersion);
                NativeSqliteHelper.InsertKeyValue(db, "ToolVersion", toolVersion);
                NativeSqliteHelper.InsertKeyValue(db, "CaseUUID", caseUuid);
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }
        }

        // 패키지(앱)의 버전 정보 가져오기 (ToolVersion 용)
        private static string GetToolVersion()
        {
            try
            {
                var v = Package.Current.Id.Version;
                return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                return "1.0.0.0";
            }
        }
    }
}