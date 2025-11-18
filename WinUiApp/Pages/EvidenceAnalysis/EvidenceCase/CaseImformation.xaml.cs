using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using WinRT.Interop;

using WinUiApp.Interop;

namespace WinUiApp.Pages.ArtifactsAnalysis
{
    public sealed partial class CaseImformation : Page
    {
        // ─────────────────────────────────────────────
        //  내부용 모델: evidence_source 행
        // ─────────────────────────────────────────────
        private class EvidenceSourceItem
        {
            public long Id { get; set; }
            public string Type { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        // ─────────────────────────────────────────────
        //  페이지 간 이동 후에도 유지할 static 상태
        // ─────────────────────────────────────────────
        private static string? _savedCaseRoot;
        private static string? _savedCaseName;
        private static string? _savedCaseCreateTime;
        private static string? _savedTimezone;

        private bool _isLoading = false;

        // 현재 케이스 루트 폴더 (예: C:\...\Cases\DFMA-Case-001)
        private string? _currentCaseRoot;

        public CaseImformation()
        {
            this.InitializeComponent();

            // 이전에 보던 내용이 있으면 복원
            _isLoading = true;
            LoadSavedState();
            _isLoading = false;

            // 처음에는 케이스가 로드되지 않았다고 보고, 표준 시간 변경 비활성화
            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            // 타임존 메뉴 초기 구성 (저장된 TimezoneTextBox 내용을 기준으로 선택 표시)
            BuildTimezoneMenu();

            // saved 상태에서 케이스 경로가 있었다면 DB 다시 로드
            if (!string.IsNullOrEmpty(_savedCaseRoot) && Directory.Exists(_savedCaseRoot))
            {
                _currentCaseRoot = _savedCaseRoot;
                _ = LoadCaseInfoFromCurrentRootAsync();
            }
        }

        // Navigation 시 전달되는 파라미터 처리 (EvidenceProcess 등에서 케이스 절대 경로를 넘겨준 경우)
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var caseRoot = e.Parameter as string;

            if (!string.IsNullOrEmpty(caseRoot) && Directory.Exists(caseRoot))
            {
                _currentCaseRoot = caseRoot;
                CaseFolderPathTextBox.Text = caseRoot;

                // EvidenceProcess 에서 온 경우에는 "찾아보기..." 비활성화
                BrowseCaseFolderButton.IsEnabled = false;

                // 실제 로드가 끝나기 전까지는 표준 시간 변경 비활성화
                if (TimezoneMenuItem != null)
                    TimezoneMenuItem.IsEnabled = false;

                _ = LoadCaseInfoFromCurrentRootAsync();
            }
        }

        // ─────────────────────────────────────────────
        //  상태 저장 / 불러오기
        // ─────────────────────────────────────────────
        private void SaveState()
        {
            if (_isLoading) return;

            _savedCaseRoot = CaseFolderPathTextBox.Text;
            _savedCaseName = CaseNameTextBox.Text;
            _savedCaseCreateTime = CaseCreateTimeTextBox.Text;
            _savedTimezone = TimezoneTextBox.Text;
        }

        private void LoadSavedState()
        {
            CaseFolderPathTextBox.Text = _savedCaseRoot ?? string.Empty;
            CaseNameTextBox.Text = _savedCaseName ?? string.Empty;
            CaseCreateTimeTextBox.Text = _savedCaseCreateTime ?? string.Empty;
            TimezoneTextBox.Text = _savedTimezone ?? string.Empty;
        }

        // UI 필드 초기화
        private void ClearCaseInfoFields()
        {
            if (string.IsNullOrEmpty(_currentCaseRoot))
                CaseFolderPathTextBox.Text = string.Empty;

            CaseNameTextBox.Text = string.Empty;
            CaseCreateTimeTextBox.Text = string.Empty;
            CaseCreateTimeTextBox.Tag = null;
            TimezoneTextBox.Text = string.Empty;

            EvidenceSourceListView.ItemsSource = null;

            // 케이스 정보가 없는 상태이므로 표준 시간 변경 비활성화
            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            SaveState();
        }

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

        // ─────────────────────────────────────────────
        //  "찾아보기..." 버튼 (시작 페이지에서 들어온 경우 사용)
        // ─────────────────────────────────────────────
        private async void BrowseCaseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            // 케이스 DB 선택 (DFMA-Case.dfmadb 등)
            picker.FileTypeFilter.Add(".dfmadb");

            var file = await picker.PickSingleFileAsync();
            if (file == null)
                return;

            var caseRoot = Path.GetDirectoryName(file.Path);
            if (string.IsNullOrEmpty(caseRoot))
            {
                await ShowMessageAsync("경로 오류", "선택한 케이스 파일의 폴더 경로를 확인할 수 없습니다.");
                return;
            }

            _currentCaseRoot = caseRoot;
            CaseFolderPathTextBox.Text = caseRoot;

            // 새 케이스 로드 전까지는 표준 시간 변경 비활성화
            if (TimezoneMenuItem != null)
                TimezoneMenuItem.IsEnabled = false;

            await LoadCaseInfoFromCurrentRootAsync();
        }

        // ─────────────────────────────────────────────
        //  케이스 정보 + Source Image 정보 로드
        // ─────────────────────────────────────────────
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
                // sqlite3.dll 로드 (이미 로드되어 있으면 내부에서 무시)
                try
                {
                    NativeDllManager.LoadNativeLibrary("sqlite3.dll", @"dll");
                }
                catch
                {
                    // 이미 로드된 경우 등은 무시
                }

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
                    // case_info 테이블 읽기
                    var info = SelectAllCaseInfo(db);

                    info.TryGetValue("CaseName", out var caseName);
                    info.TryGetValue("CaseCreateTime", out var caseCreateTimeRaw);
                    info.TryGetValue("Timezone", out var timezone);

                    CaseNameTextBox.Text = caseName ?? string.Empty;
                    TimezoneTextBox.Text = timezone ?? string.Empty;

                    // DB의 원본 CaseCreateTime 문자열은 Tag에 보관하고,
                    // TextBox에는 Timezone을 고려한 표시용 시간만 넣는다.
                    CaseCreateTimeTextBox.Tag = caseCreateTimeRaw ?? string.Empty;
                    CaseCreateTimeTextBox.Text = AdjustCaseCreateTimeForTimezone(
                        caseCreateTimeRaw,
                        timezone
                    );

                    // evidence_source 테이블 읽기 (StaticImage 타입만)
                    var evidenceList = SelectEvidenceSourceStaticImage(db);
                    EvidenceSourceListView.ItemsSource = evidenceList;

                    // 타임존 메뉴 다시 구성 (현 TimezoneTextBox 내용 기준으로 체크 상태 동기화)
                    BuildTimezoneMenu();

                    // 케이스가 정상적으로 로드되었으므로 표준 시간 변경 가능
                    if (TimezoneMenuItem != null)
                        TimezoneMenuItem.IsEnabled = true;

                    // 현재 UI 상태를 static에 저장
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

        // ─────────────────────────────────────────────
        //  타임존 선택 메뉴 (MenuBarItem + RadioMenuFlyoutItem)
        // ─────────────────────────────────────────────
        private void BuildTimezoneMenu()
        {
            if (TimezoneMenuItem == null)
                return;

            string currentDisplay = TimezoneTextBox.Text;

            TimezoneMenuItem.Items.Clear();

            // 모든 시스템 타임존을 표시 (예: (UTC+09:00) Seoul ...)
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

        private async void TimezoneMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioMenuFlyoutItem item)
                return;

            // 혹시라도 케이스가 없는 상태에서 클릭되는 것을 방지 (안전장치)
            if (string.IsNullOrEmpty(_currentCaseRoot))
            {
                if (TimezoneMenuItem != null)
                    TimezoneMenuItem.IsEnabled = false;

                await ShowMessageAsync("케이스 정보 없음", "먼저 케이스를 로드한 뒤 표준 시간을 변경할 수 있습니다.");
                return;
            }

            string display = item.Text ?? string.Empty;
            TimezoneTextBox.Text = display;

            // DB에 저장된 원본 CaseCreateTime (Tag에 보관된 값)을 기준으로
            // 새 타임존을 적용한 표시용 시간을 재계산
            string rawCreateTime = (CaseCreateTimeTextBox.Tag as string) ?? CaseCreateTimeTextBox.Text;
            CaseCreateTimeTextBox.Text = AdjustCaseCreateTimeForTimezone(rawCreateTime, display);

            SaveState();

            await UpdateTimezoneInDbAsync(display);
        }

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
                try
                {
                    NativeDllManager.LoadNativeLibrary("sqlite3.dll", @"dll");
                }
                catch
                {
                    // 이미 로드된 경우 등은 무시
                }

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

        // ─────────────────────────────────────────────
        //  Timezone 기반 CaseCreateTime 표시용 변환
        // ─────────────────────────────────────────────
        private string AdjustCaseCreateTimeForTimezone(string? caseCreateTimeRaw, string? timezoneDisplay)
        {
            if (string.IsNullOrWhiteSpace(caseCreateTimeRaw) || string.IsNullOrWhiteSpace(timezoneDisplay))
                return caseCreateTimeRaw ?? string.Empty;

            // DB에 저장된 CaseCreateTime은 "기준 시간(예: UTC)"이라고 가정
            // 파싱 시 우선 UTC로 가정
            if (!DateTime.TryParse(
                    caseCreateTimeRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var utcBase))
            {
                // 실패하면 로컬 기준으로라도 파싱 시도
                if (!DateTime.TryParse(caseCreateTimeRaw, out utcBase))
                    return caseCreateTimeRaw;
                utcBase = utcBase.ToUniversalTime();
            }

            if (!TryParseUtcOffsetFromDisplay(timezoneDisplay, out var offset))
                return caseCreateTimeRaw;

            var localTime = utcBase + offset;
            return localTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// "(UTC+09:00) Seoul" 또는 "UTC+09:00" 같은 문자열에서 UTC offset 파싱
        /// </summary>
        private bool TryParseUtcOffsetFromDisplay(string? timezoneDisplay, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(timezoneDisplay))
                return false;

            string candidate = timezoneDisplay.Trim();

            // "(UTC+09:00) Seoul" 형식에서 괄호 안 부분만 추출
            int openIdx = candidate.IndexOf("(UTC", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                int closeIdx = candidate.IndexOf(')', openIdx);
                if (closeIdx > openIdx)
                {
                    candidate = candidate.Substring(openIdx + 1, closeIdx - openIdx - 1); // "UTC+09:00"
                }
            }

            // 그냥 "UTC+09:00" 으로 들어온 경우도 처리
            if (!candidate.StartsWith("UTC", StringComparison.OrdinalIgnoreCase))
                return false;

            candidate = candidate.Substring(3).Trim(); // "+09:00" 또는 "-03:30" 등

            int sign = 1;
            if (candidate.StartsWith("+"))
            {
                candidate = candidate.Substring(1);
            }
            else if (candidate.StartsWith("-"))
            {
                sign = -1;
                candidate = candidate.Substring(1);
            }

            var parts = candidate.Split(':');
            if (parts.Length == 0 || parts.Length > 2)
                return false;

            if (!int.TryParse(parts[0], out int hours))
                return false;

            int minutes = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], out minutes))
                return false;

            offset = new TimeSpan(sign * hours, sign * minutes, 0);
            return true;
        }

        // ─────────────────────────────────────────────
        //  SQLite sqlite3_exec P/Invoke 및 콜백
        // ─────────────────────────────────────────────
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

        // case_info 테이블: key, value (TEXT)
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

        // evidence_source 테이블: id, type, value
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
    }
}
