using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Windows.Storage.Pickers;
using WinRT.Interop;

using WinUiApp.Interop;
using WinUiApp.Services;

namespace WinUiApp.Pages.ArtifactsAnalysis
{
    // 페이지에서 케이스 정보 및 타임존을 관리하는 UI Page
    public sealed partial class CaseImformation : Page
    {
        

        public static string? CurrentCaseRoot { get; private set; }
        public static string? CurrentDbPath { get; private set; }
        public static string? CurrentTimezoneDisplay { get; private set; }

        // evidence_source 행 모델
        private class EvidenceSourceItem
        {
            public long Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        // 페이지 간 상태 저장용 static 데이터
        private static string? _savedCaseRoot;
        private static string? _savedCaseName;
        private static string? _savedCaseCreateTime;
        private static string? _savedTimezone;

        private bool _isLoading = false;

        // 현재 케이스 루트 경로 저장
        private string? _currentCaseRoot;

        // 페이지 초기화 및 이전 상태 복원
        public CaseImformation()
        {
            this.InitializeComponent();

            _isLoading = true;
            LoadSavedState();
            _isLoading = false;

            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            BuildTimezoneMenu();

            if (!string.IsNullOrEmpty(_savedCaseRoot) && Directory.Exists(_savedCaseRoot))
            {
                _currentCaseRoot = _savedCaseRoot;

                CurrentCaseRoot = _currentCaseRoot;
                CurrentDbPath = Path.Combine(_currentCaseRoot, "DFMA-Case.dfmadb");
                CurrentTimezoneDisplay = _savedTimezone;

                _ = LoadCaseInfoFromCurrentRootAsync();
            }
        }

        // 네비게이션 시 케이스 루트를 받아 초기화
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var caseRoot = e.Parameter as string;

            if (!string.IsNullOrEmpty(caseRoot) && Directory.Exists(caseRoot))
            {
                _currentCaseRoot = caseRoot;
                CaseFolderPathTextBox.Text = caseRoot;

                CurrentCaseRoot = _currentCaseRoot;
                CurrentDbPath = Path.Combine(_currentCaseRoot, "DFMA-Case.dfmadb");

                BrowseCaseFolderButton.IsEnabled = false;

                if (TimezoneMenuItem != null)
                    TimezoneMenuItem.IsEnabled = false;

                _ = LoadCaseInfoFromCurrentRootAsync();
            }
        }

        // 현재 상태를 static 변수에 저장
        private void SaveState()
        {
            if (_isLoading) return;

            _savedCaseRoot = CaseFolderPathTextBox.Text;
            _savedCaseName = CaseNameTextBox.Text;
            _savedCaseCreateTime = CaseCreateTimeTextBox.Text;
            _savedTimezone = TimezoneTextBox.Text;

            CurrentCaseRoot = _savedCaseRoot;
            if (!string.IsNullOrEmpty(CurrentCaseRoot))
                CurrentDbPath = Path.Combine(CurrentCaseRoot, "DFMA-Case.dfmadb");
            CurrentTimezoneDisplay = _savedTimezone;
        }

        // static 저장된 상태를 UI에 반영
        private void LoadSavedState()
        {
            CaseFolderPathTextBox.Text = _savedCaseRoot ?? string.Empty;
            CaseNameTextBox.Text = _savedCaseName ?? string.Empty;
            CaseCreateTimeTextBox.Text = _savedCaseCreateTime ?? string.Empty;
            TimezoneTextBox.Text = _savedTimezone ?? string.Empty;
        }

        // UI 입력값 초기화 및 static 상태 초기화
        private void ClearCaseInfoFields()
        {
            if (string.IsNullOrEmpty(_currentCaseRoot))
                CaseFolderPathTextBox.Text = string.Empty;

            CaseNameTextBox.Text = string.Empty;
            CaseCreateTimeTextBox.Text = string.Empty;
            CaseCreateTimeTextBox.Tag = null;
            TimezoneTextBox.Text = string.Empty;

            EvidenceSourceListView.ItemsSource = null;

            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            CurrentCaseRoot = null;
            CurrentDbPath = null;
            CurrentTimezoneDisplay = null;

            SaveState();
        }

        // 간단한 메시지 다이얼로그 표시
        private async Task ShowMessageAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = this.XamlRoot,
                Title = title,
                Content = content,
                CloseButtonText = "확인"
            };
            await dialog.ShowAsync();
        }

        // 케이스 DB 파일 선택 후 로드
        private async void BrowseCaseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".dfmadb");

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            var caseRoot = Path.GetDirectoryName(file.Path);
            if (string.IsNullOrEmpty(caseRoot))
            {
                await ShowMessageAsync("경로 오류", "선택한 케이스 파일의 폴더 경로를 확인할 수 없습니다.");
                return;
            }

            _currentCaseRoot = caseRoot;
            CaseFolderPathTextBox.Text = caseRoot;

            CurrentCaseRoot = _currentCaseRoot;
            CurrentDbPath = Path.Combine(_currentCaseRoot, "DFMA-Case.dfmadb");

            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            await LoadCaseInfoFromCurrentRootAsync();
        }

        // 케이스 정보 + 정적 이미지 소스 정보를 DB에서 읽어 UI에 반영
        private async Task LoadCaseInfoFromCurrentRootAsync()
        {
            if (string.IsNullOrEmpty(_currentCaseRoot))
            {
                ClearCaseInfoFields();
                return;
            }

            string dbPath = Path.Combine(_currentCaseRoot, "DFMA-Case.dfmadb");
            if (!File.Exists(dbPath))
            {
                ClearCaseInfoFields();
                await ShowMessageAsync("DB 없음", $"케이스 DB 파일을 찾을 수 없습니다.\n경로: {dbPath}");
                return;
            }

            try
            {
                try { NativeDllManager.LoadNativeLibrary("sqlite3.dll", @"dll"); } catch { }

                IntPtr db;
                int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
                int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);

                if (rc != NativeSqliteHelper.SQLITE_OK)
                {
                    ClearCaseInfoFields();
                    await ShowMessageAsync("DB 열기 실패",
                        $"데이터베이스를 열 수 없습니다.\n경로: {dbPath}\nrc={rc}");
                    return;
                }

                try
                {
                    var info = SelectAllCaseInfo(db);

                    info.TryGetValue("CaseName", out var caseName);
                    info.TryGetValue("CaseCreateTime", out var caseCreateTimeRaw);
                    info.TryGetValue("Timezone", out var timezone);

                    CaseNameTextBox.Text = caseName ?? string.Empty;
                    TimezoneTextBox.Text = timezone ?? string.Empty;

                    CaseCreateTimeTextBox.Tag = caseCreateTimeRaw ?? string.Empty;
                    CaseCreateTimeTextBox.Text = AdjustCaseCreateTimeForTimezone(
                        caseCreateTimeRaw,
                        timezone
                    );

                    var evidenceList = SelectEvidenceSourceStaticImage(db);
                    EvidenceSourceListView.ItemsSource = evidenceList;

                    BuildTimezoneMenu();

                    if (TimezoneMenuItem != null)
                        TimezoneMenuItem.IsEnabled = true;

                    CurrentCaseRoot = _currentCaseRoot;
                    CurrentDbPath = dbPath;
                    CurrentTimezoneDisplay = timezone;

                    SaveState();
                }
                finally
                {
                    NativeSqliteHelper.sqlite3_close(db);
                }
            }
            catch (Exception ex)
            {
                ClearCaseInfoFields();
                await ShowMessageAsync(
                    "케이스 정보 로드 오류",
                    $"케이스 정보를 읽는 중 오류가 발생했습니다.\n{ex.Message}");
            }
        }

        // 타임존 설정 메뉴 구성
        private void BuildTimezoneMenu()
        {
            if (TimezoneMenuItem == null) return;

            string currentDisplay = TimezoneTextBox.Text;
            TimezoneMenuItem.Items.Clear();

            foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            {
                var item = new RadioMenuFlyoutItem
                {
                    Text = tz.DisplayName,
                    Tag = tz.Id,
                    GroupName = "TimezoneGroup",
                    IsChecked = !string.IsNullOrEmpty(currentDisplay) &&
                                string.Equals(currentDisplay, tz.DisplayName, StringComparison.OrdinalIgnoreCase)
                };

                item.Click += TimezoneMenuItem_Click;
                TimezoneMenuItem.Items.Add(item);
            }
        }

        // 선택된 타임존을 DB 및 UI에 반영
        private async void TimezoneMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item) return;

            if (string.IsNullOrEmpty(_currentCaseRoot))
            {
                if (TimezoneMenuItem != null)
                    TimezoneMenuItem.IsEnabled = false;

                await ShowMessageAsync("케이스 정보 없음", "먼저 케이스를 로드한 뒤 표준 시간을 변경할 수 있습니다.");
                return;
            }

            string display = item.Text ?? string.Empty;
            TimezoneTextBox.Text = display;

            string rawCreateTime = (CaseCreateTimeTextBox.Tag as string) ?? CaseCreateTimeTextBox.Text;
            CaseCreateTimeTextBox.Text = AdjustCaseCreateTimeForTimezone(rawCreateTime, display);

            CurrentTimezoneDisplay = display;

            SaveState();
            await UpdateTimezoneInDbAsync(display);
        }

        // DB의 타임존 값을 업데이트
        private async Task UpdateTimezoneInDbAsync(string timezoneDisplay)
        {
            if (string.IsNullOrEmpty(_currentCaseRoot))
            {
                await ShowMessageAsync("케이스 정보 없음", "케이스 폴더 정보를 찾을 수 없습니다. 먼저 케이스를 선택해 주세요.");
                return;
            }

            string dbPath = Path.Combine(_currentCaseRoot, "DFMA-Case.dfmadb");
            if (!File.Exists(dbPath))
            {
                await ShowMessageAsync("DB 없음", $"케이스 DB 파일을 찾을 수 없습니다.\n경로: {dbPath}");
                return;
            }

            try
            {
                try { NativeDllManager.LoadNativeLibrary("sqlite3.dll", @"dll"); } catch { }

                IntPtr db;
                int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
                int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);

                if (rc != NativeSqliteHelper.SQLITE_OK)
                {
                    await ShowMessageAsync("DB 열기 실패",
                        $"데이터베이스를 열 수 없습니다.\n경로: {dbPath}\nrc={rc}");
                    return;
                }

                try
                {
                    string safeTz = (timezoneDisplay ?? string.Empty).Replace("'", "''");
                    string sql =
                        "INSERT OR REPLACE INTO case_info(key, value) " +
                        $"VALUES('Timezone', '{safeTz}');";

                    NativeSqliteHelper.ExecNonQuery(db, sql);

                    CurrentTimezoneDisplay = timezoneDisplay;
                }
                finally
                {
                    NativeSqliteHelper.sqlite3_close(db);
                }
            }
            catch (Exception ex)
            {
                await ShowMessageAsync(
                    "타임존 저장 오류",
                    $"타임존 정보를 저장하는 중 오류가 발생했습니다.\n{ex.Message}");
            }
        }

        // 타임존 기준으로 케이스 생성 시각 표시 변환
        private string AdjustCaseCreateTimeForTimezone(string? caseCreateTimeRaw, string? timezoneDisplay)
        {
            if (string.IsNullOrWhiteSpace(caseCreateTimeRaw) || string.IsNullOrWhiteSpace(timezoneDisplay))
                return caseCreateTimeRaw ?? string.Empty;

            if (!DateTime.TryParse(
                    caseCreateTimeRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utcBase))
            {
                if (!DateTime.TryParse(caseCreateTimeRaw, out utcBase))
                    return caseCreateTimeRaw;

                utcBase = utcBase.ToUniversalTime();
            }

            if (!TryParseUtcOffsetFromDisplay(timezoneDisplay, out var offset))
                return caseCreateTimeRaw;

            var localTime = utcBase + offset;
            return localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // "UTC(+09:00)" 등에서 시차(TimeSpan)를 파싱
        private bool TryParseUtcOffsetFromDisplay(string? timezoneDisplay, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(timezoneDisplay)) return false;

            string candidate = timezoneDisplay.Trim();

            int openIdx = candidate.IndexOf("(UTC", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                int closeIdx = candidate.IndexOf(')', openIdx);
                if (closeIdx > openIdx)
                {
                    candidate = candidate.Substring(openIdx + 1, closeIdx - openIdx - 1);
                }
            }

            if (!candidate.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
                return false;

            candidate = candidate.Substring(3).Trim();

            int sign = 1;
            if (candidate.StartsWith("+")) candidate = candidate.Substring(1);
            else if (candidate.StartsWith("-"))
            {
                sign = -1;
                candidate = candidate.Substring(1);
            }

            var parts = candidate.Split(':');
            if (parts.Length == 0 || parts.Length > 2) return false;
            if (!int.TryParse(parts[0], out int hours)) return false;

            int minutes = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], out minutes)) return false;

            offset = new TimeSpan(sign * hours, sign * minutes, 0);
            return true;
        }

        // case_info 테이블 전체 읽기
        private Dictionary<string, string> SelectAllCaseInfo(IntPtr db)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];

                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                string? key = null;
                string? value = null;

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = Marshal.PtrToStringAnsi(namePtrs[i]) ?? string.Empty;
                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? string.Empty
                        : (Marshal.PtrToStringAnsi(valuePtrs[i]) ?? string.Empty);

                    if (colName.Equals("key", StringComparison.OrdinalIgnoreCase))
                        key = colVal;
                    else if (colName.Equals("value", StringComparison.OrdinalIgnoreCase))
                        value = colVal;
                }

                if (!string.IsNullOrEmpty(key))
                    result[key!] = value ?? string.Empty;

                return 0;
            };

            IntPtr errPtr;
            int rc = sqlite3_exec(
                db,
                "SELECT key, value FROM case_info;",
                callback,
                IntPtr.Zero,
                out errPtr);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                string message = $"SQLite SELECT 오류 (case_info, rc={rc})";
                if (errPtr != IntPtr.Zero)
                {
                    message += ": " + Marshal.PtrToStringAnsi(errPtr);
                    NativeSqliteHelper.sqlite3_free(errPtr);
                }
                throw new InvalidOperationException(message);
            }

            return result;
        }

        // evidence_source 테이블에서 정적 이미지 목록 읽기
        private List<EvidenceSourceItem> SelectEvidenceSourceStaticImage(IntPtr db)
        {
            var list = new List<EvidenceSourceItem>();

            ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];

                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                long id = 0;
                string? type = null;
                string? value = null;

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = Marshal.PtrToStringAnsi(namePtrs[i]) ?? string.Empty;
                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? string.Empty
                        : (Marshal.PtrToStringAnsi(valuePtrs[i]) ?? string.Empty);

                    if (colName.Equals("id", StringComparison.OrdinalIgnoreCase))
                        long.TryParse(colVal, out id);
                    else if (colName.Equals("type", StringComparison.OrdinalIgnoreCase))
                        type = colVal;
                    else if (colName.Equals("value", StringComparison.OrdinalIgnoreCase))
                        value = colVal;
                }

                if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(value))
                {
                    list.Add(new EvidenceSourceItem
                    {
                        Id = id,
                        Type = type!,
                        Value = value!
                    });
                }

                return 0;
            };

            IntPtr errPtr;
            int rc = sqlite3_exec(
                db,
                "SELECT id, type, value FROM evidence_source WHERE type = 'StaticImage';",
                callback,
                IntPtr.Zero,
                out errPtr);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                string message = $"SQLite SELECT 오류 (evidence_source, rc={rc})";
                if (errPtr != IntPtr.Zero)
                {
                    message += ": " + Marshal.PtrToStringAnsi(errPtr);
                    NativeSqliteHelper.sqlite3_free(errPtr);
                }
                throw new InvalidOperationException(message);
            }

            return list;
        }

        // sqlite3_exec 델리게이트 및 extern 선언
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ExecCallback(
            IntPtr arg,
            int columnCount,
            IntPtr columnValues,
            IntPtr columnNames);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int sqlite3_exec(
            IntPtr db,
            string sql,
            ExecCallback callback,
            IntPtr arg,
            out IntPtr errMsg);
    }
}
