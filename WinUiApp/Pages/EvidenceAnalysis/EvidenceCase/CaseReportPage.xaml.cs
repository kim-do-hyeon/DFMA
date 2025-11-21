using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUiApp.Interop;

namespace WinUiApp.Pages.ArtifactsAnalysis
{
    public sealed partial class CaseReportPage : Page
    {
        private string? _caseRoot;

        public CaseReportPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var caseRoot = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(caseRoot) || !Directory.Exists(caseRoot))
            {
                ShowEmptyState("케이스 폴더 경로를 확인할 수 없습니다.");
                return;
            }

            _caseRoot = caseRoot;
            CasePathTextBlock.Text = caseRoot;
            _ = LoadReportAsync();
        }

        private async Task LoadReportAsync()
        {
            if (string.IsNullOrWhiteSpace(_caseRoot))
            {
                ShowEmptyState("케이스 경로가 지정되지 않았습니다.");
                return;
            }

            ToggleLoading(true);
            EmptyStateText.Visibility = Visibility.Collapsed;

            try
            {
                var data = await Task.Run(() => LoadReportData(_caseRoot!));

                CaseInfoListView.ItemsSource = data.CaseInfo;
                EvidenceListView.ItemsSource = data.EvidenceSources;
                EventLogListView.ItemsSource = data.EventLogs;

                EmptyStateText.Visibility = (data.CaseInfo.Count == 0 &&
                                             data.EvidenceSources.Count == 0 &&
                                             data.EventLogs.Count == 0)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (EmptyStateText.Visibility == Visibility.Visible)
                {
                    EmptyStateText.Text = "표시할 데이터가 없습니다.";
                }

                if (data.EventLogs.Count > 0)
                {
                    if (DetailsListView.SelectedIndex < 0)
                    {
                        DetailsListView.SelectedIndex = 0;
                    }
                    else
                    {
                        UpdateDetailContent();
                    }
                }
                else
                {
                    DetailsListView.SelectedIndex = -1;
                    UpdateDetailContent();
                }
            }
            catch (Exception ex)
            {
                ShowEmptyState($"케이스 정보를 불러오지 못했습니다.{Environment.NewLine}{ex.Message}");
            }
            finally
            {
                ToggleLoading(false);
            }
        }

        private ReportData LoadReportData(string caseRoot)
        {
            string dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");
            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException("케이스 DB 파일을 찾을 수 없습니다.", dbPath);
            }

            try
            {
                NativeDllManager.LoadNativeLibrary("sqlite3.dll", @"dll");
            }
            catch
            {
                // 이미 로드된 경우 무시
            }

            IntPtr db;
            int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
            int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                throw new InvalidOperationException($"데이터베이스를 열 수 없습니다. rc={rc}");
            }

            try
            {
                var caseInfo = SelectAllCaseInfo(db);
                var evidence = SelectEvidenceSources(db);
                var events = SelectEventLogs(db);

                return new ReportData(caseInfo, evidence, events);
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }
        }

        private void ShowEmptyState(string message)
        {
            CaseInfoListView.ItemsSource = null;
            EvidenceListView.ItemsSource = null;
            EventLogListView.ItemsSource = null;

            EmptyStateText.Text = message;
            EmptyStateText.Visibility = Visibility.Visible;
        }

        private void ToggleLoading(bool isActive)
        {
            LoadingRing.IsActive = isActive;
            LoadingRing.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance is MainWindow window)
            {
                window.RootFrameControl.Navigate(typeof(StartPage));
            }
        }

        private void DetailsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDetailContent();
        }

        private void UpdateDetailContent()
        {
            if (DetailsListView.SelectedItem is ListViewItem item &&
                item.Tag is string tag &&
                tag == "EventLogs")
            {
                EventLogCard.Visibility = Visibility.Visible;
            }
            else
            {
                EventLogCard.Visibility = Visibility.Collapsed;
            }
        }

        #region SQLite helpers

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

        private List<CaseInfoEntry> SelectAllCaseInfo(IntPtr db)
        {
            var list = new List<CaseInfoEntry>();

            ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];
                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                var entry = new CaseInfoEntry();

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = Marshal.PtrToStringAnsi(namePtrs[i]) ?? string.Empty;
                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? string.Empty
                        : (Marshal.PtrToStringAnsi(valuePtrs[i]) ?? string.Empty);

                    if (colName.Equals("key", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Key = colVal;
                    }
                    else if (colName.Equals("value", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.Value = colVal;
                    }
                }

                if (!string.IsNullOrEmpty(entry.Key))
                {
                    list.Add(entry);
                }

                return 0;
            };

            IntPtr errPtr;
            int rc = sqlite3_exec(
                db,
                "SELECT key, value FROM case_info ORDER BY key;",
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

            return list;
        }

        private List<EvidenceSourceEntry> SelectEvidenceSources(IntPtr db)
        {
            var list = new List<EvidenceSourceEntry>();

            ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];
                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                var entry = new EvidenceSourceEntry();

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = Marshal.PtrToStringAnsi(namePtrs[i]) ?? string.Empty;
                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? string.Empty
                        : (Marshal.PtrToStringAnsi(valuePtrs[i]) ?? string.Empty);

                    switch (colName.ToLowerInvariant())
                    {
                        case "id":
                            long.TryParse(colVal, out var id);
                            entry.Id = id;
                            break;
                        case "type":
                            entry.Type = colVal;
                            break;
                        case "value":
                            entry.Value = colVal;
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(entry.Type))
                {
                    list.Add(entry);
                }

                return 0;
            };

            IntPtr errPtr;
            int rc = sqlite3_exec(
                db,
                "SELECT id, type, value FROM evidence_source ORDER BY id;",
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

        private List<EventLogEntry> SelectEventLogs(IntPtr db)
        {
            var list = new List<EventLogEntry>();

            ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];
                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                var entry = new EventLogEntry();

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = Marshal.PtrToStringAnsi(namePtrs[i]) ?? string.Empty;
                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? string.Empty
                        : (Marshal.PtrToStringAnsi(valuePtrs[i]) ?? string.Empty);

                    switch (colName.ToLowerInvariant())
                    {
                        case "id":
                            long.TryParse(colVal, out var id);
                            entry.Id = id;
                            break;
                        case "event_time_utc":
                            entry.EventTimeUtc = colVal;
                            break;
                        case "level":
                            entry.Level = colVal;
                            break;
                        case "source":
                            entry.Source = colVal;
                            break;
                        case "message":
                            entry.Message = colVal;
                            break;
                    }
                }

                list.Add(entry);
                return 0;
            };

            IntPtr errPtr;
            int rc = sqlite3_exec(
                db,
                "SELECT id, event_time_utc, level, source, message FROM artifacts_eventlog ORDER BY id;",
                callback,
                IntPtr.Zero,
                out errPtr);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                string message = $"SQLite SELECT 오류 (artifacts_eventlog, rc={rc})";
                if (errPtr != IntPtr.Zero)
                {
                    message += ": " + Marshal.PtrToStringAnsi(errPtr);
                    NativeSqliteHelper.sqlite3_free(errPtr);
                }
                throw new InvalidOperationException(message);
            }

            return list;
        }

        #endregion
    }

    public sealed class CaseInfoEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class EvidenceSourceEntry
    {
        public long Id { get; set; }
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    public sealed class EventLogEntry
    {
        public long Id { get; set; }
        public string EventTimeUtc { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    internal sealed record ReportData(
        List<CaseInfoEntry> CaseInfo,
        List<EvidenceSourceEntry> EvidenceSources,
        List<EventLogEntry> EventLogs);
}

