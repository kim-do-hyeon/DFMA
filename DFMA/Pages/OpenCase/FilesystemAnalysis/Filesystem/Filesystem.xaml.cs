using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

using WinUiApp;
using WinUiApp.Services;
using WinUiApp.Interop;
using WinUiApp.Pages.ArtifactsAnalysis;

#nullable enable

namespace WinUiApp.Pages.OpenCase.FilesystemAnalysis.Filesystem
{
    // 디렉터리 TableView 한 행을 표현하는 데이터 모델
    public sealed class DirectoryEntryRow
    {
        public ExplorerItem Item { get; init; } = null!;
        public FileSystemTreeServiceCore Svc { get; init; } = null!;

        public string Name { get; init; } = "";
        public string TypeText { get; init; } = "";
        public string SizeText { get; init; } = "";
        public string CreateText { get; init; } = "";
        public string ModifyText { get; init; } = "";
        public string AccessText { get; init; } = "";
        public string MftModifyText { get; init; } = "";
        public string MftEntryText { get; init; } = "";

        public bool IsFolder { get; init; }
        public bool IsFile => !IsFolder;
    }

    // 파일 시스템 브라우저 페이지: 다중 이미지 로드 + TreeView + TableView 통합 UI
    public sealed partial class Filesystem : Page
    {
        private static string? _savedCaseRoot;
        private static List<string> _savedImagePaths = new();
        private static ExplorerItem? _savedSelectedItem;
        private static string? _savedSelectedImagePath;

        private readonly List<FileSystemTreeServiceCore> _services = new();

        private readonly Dictionary<TreeViewNode, FileSystemTreeServiceCore> _rootSvcMap = new();
        private readonly Dictionary<ExplorerItem, FileSystemTreeServiceCore> _itemSvcMap =
            new Dictionary<ExplorerItem, FileSystemTreeServiceCore>(new ExplorerItemRefComparer());

        private readonly Dictionary<FileSystemTreeServiceCore, string> _svcImagePath = new();

        // ExplorerItem 참조 기반 Dictionary 키 비교용 comparer
        private sealed class ExplorerItemRefComparer : IEqualityComparer<ExplorerItem>
        {
            public bool Equals(ExplorerItem? x, ExplorerItem? y) => ReferenceEquals(x, y);
            public int GetHashCode(ExplorerItem obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private readonly NtfsMetadataService _metaSvc = new();
        private readonly ObservableCollection<DirectoryEntryRow> _entries = new();

        private ExplorerItem? _currentDirectory;
        private FileSystemTreeServiceCore? _currentSvc;

        private DirectoryEntryRow? _contextRow;

        private readonly Stack<(ExplorerItem item, FileSystemTreeServiceCore svc)> _backStack = new();
        private readonly Stack<(ExplorerItem item, FileSystemTreeServiceCore svc)> _forwardStack = new();
        private bool _suppressHistoryRecord = false;

        private TimeSpan? _currentOffset;

        private bool HasMultipleImages => _services.Count > 1;

        // 생성자: UI 초기화 + 이벤트 연결
        public Filesystem()
        {
            InitializeComponent();
            DirectoryTableView.ItemsSource = _entries;

            try { FileTreeView.SelectionMode = TreeViewSelectionMode.Single; } catch { }

            this.PointerPressed += Filesystem_PointerPressed;
        }

        // 페이지 진입 시 이미지 로드/상태 복원
        protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var caseRoot = CaseImformation.CurrentCaseRoot;
            if (string.IsNullOrEmpty(caseRoot) || !Directory.Exists(caseRoot))
                return;

            if (!string.Equals(_savedCaseRoot, caseRoot, StringComparison.OrdinalIgnoreCase))
            {
                _savedCaseRoot = caseRoot;
                _savedImagePaths = new List<string>();
                _savedSelectedItem = null;
                _savedSelectedImagePath = null;
            }

            _currentOffset = ParseOffset(CaseImformation.CurrentTimezoneDisplay);

            if (_savedImagePaths.Count > 0)
            {
                LoadMultipleDiskImages(_savedImagePaths);
                RestoreLastSelection();
            }
            else
            {
                await AutoLoadDiskImagesFromCaseDbAsync();
                RestoreLastSelection();
            }
        }

        // 페이지 이탈 시 상태 저장
        protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SaveFilesystemState();
        }

        // 케이스 DB에서 StaticImage 목록 불러와 자동 로드
        private Task AutoLoadDiskImagesFromCaseDbAsync()
        {
            var caseRoot = CaseImformation.CurrentCaseRoot;
            if (string.IsNullOrEmpty(caseRoot) || !Directory.Exists(caseRoot))
                return Task.CompletedTask;

            var dbPath = CaseImformation.CurrentDbPath;
            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");

            if (!File.Exists(dbPath))
                return Task.CompletedTask;

            // Microsoft.Data.Sqlite가 자동으로 네이티브 DLL을 관리합니다.
            IntPtr db;
            int flags = NativeSqliteHelper.SQLITE_OPEN_READWRITE;
            int rc = NativeSqliteHelper.sqlite3_open_v2(dbPath, out db, flags, null);
            if (rc != NativeSqliteHelper.SQLITE_OK)
                return Task.CompletedTask;

            List<string> paths;
            try
            {
                string createTableSql =
                    "CREATE TABLE IF NOT EXISTS evidence_source (" +
                    " id INTEGER PRIMARY KEY AUTOINCREMENT," +
                    " type TEXT NOT NULL," +
                    " value TEXT NOT NULL" +
                    ");";
                NativeSqliteHelper.ExecNonQuery(db, createTableSql);

                paths = SelectEvidenceSourceStaticImagePaths(db);
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }

            var existing = paths.Where(File.Exists).ToList();
            if (existing.Count == 0)
                return Task.CompletedTask;

            LoadMultipleDiskImages(existing);
            return Task.CompletedTask;
        }

        // 여러 디스크 이미지 동시에 로드하여 TreeView 구성
        private void LoadMultipleDiskImages(List<string> paths)
        {
            _entries.Clear();
            RightHeaderTextBlock.Text = "폴더를 선택하세요";
            ResetRightViewState();

            _currentDirectory = null;
            _currentSvc = null;

            _services.Clear();
            _rootSvcMap.Clear();
            _itemSvcMap.Clear();
            _svcImagePath.Clear();
            FileTreeView.RootNodes.Clear();

            var allRoots = new List<TreeViewNode>();

            foreach (var path in paths)
            {
                try
                {
                    var svc = new FileSystemTreeServiceCore(FileTreeView, Status);
                    _services.Add(svc);
                    _svcImagePath[svc] = path;

                    svc.LoadDiskImageRoot(path);

                    var producedRoots = FileTreeView.RootNodes.ToList();
                    foreach (var root in producedRoots)
                    {
                        _rootSvcMap[root] = svc;

                        if (root.Content is ExplorerItem ei)
                            _itemSvcMap[ei] = svc;

                        allRoots.Add(root);
                    }

                    FileTreeView.RootNodes.Clear();
                }
                catch { }
            }

            foreach (var root in allRoots)
                FileTreeView.RootNodes.Add(root);

            try
            {
                FileTreeView.UpdateLayout();
                FileTreeView.SelectedNodes.Clear();
            }
            catch { }

            _savedImagePaths = paths.ToList();
        }

        // TreeView children 모두 svc 매핑하는 헬퍼
        private void RegisterChildren(TreeViewNode parent, FileSystemTreeServiceCore svc)
        {
            foreach (var child in parent.Children)
            {
                if (child.Content is ExplorerItem ei)
                    _itemSvcMap[ei] = svc;
            }
        }

        // TreeViewNode로 서비스 역추적
        private FileSystemTreeServiceCore? GetSvcForNode(TreeViewNode? node)
        {
            var original = node;
            var cur = node;

            while (cur != null)
            {
                if (_rootSvcMap.TryGetValue(cur, out var svcFromRoot))
                    return svcFromRoot;
                cur = cur.Parent;
            }

            if (original?.Content is ExplorerItem ei &&
                _itemSvcMap.TryGetValue(ei, out var svcFromItem))
                return svcFromItem;

            return null;
        }

        // 현재 선택 폴더/이미지 상태 저장
        private void SaveFilesystemState()
        {
            var caseRoot = CaseImformation.CurrentCaseRoot;
            if (string.IsNullOrEmpty(caseRoot)) return;

            _savedCaseRoot = caseRoot;

            if (_currentDirectory != null &&
                _currentSvc != null &&
                _svcImagePath.TryGetValue(_currentSvc, out var img))
            {
                _savedSelectedItem = new ExplorerItem
                {
                    Name = _currentDirectory.Name,
                    FullPath = _currentDirectory.FullPath,
                    Type = _currentDirectory.Type,
                    VolumeIndex = _currentDirectory.VolumeIndex
                };
                _savedSelectedImagePath = img;
            }
        }

        // 이전 선택 상태 복원
        private void RestoreLastSelection()
        {
            if (_savedSelectedItem == null || string.IsNullOrEmpty(_savedSelectedImagePath))
                return;

            var svc = _services.FirstOrDefault(s =>
                _svcImagePath.TryGetValue(s, out var p) &&
                string.Equals(p, _savedSelectedImagePath, StringComparison.OrdinalIgnoreCase));

            if (svc == null) return;

            _currentSvc = svc;
            _currentDirectory = _savedSelectedItem;

            PopulateRightPane(_currentDirectory, svc);

            try
            {
                var node = svc.RevealFolderInTree(_currentDirectory);
                if (node != null)
                {
                    node.IsExpanded = true;
                    FileTreeView.SelectedNodes.Clear();
                    FileTreeView.SelectedNodes.Add(node);
                }
            }
            catch { }
        }

        // 마우스 Back/Forward 버튼 처리
        private void Filesystem_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(this).Properties;

            if (props.IsXButton1Pressed)
            {
                e.Handled = true;
                NavigateFolderBack();
            }
            else if (props.IsXButton2Pressed)
            {
                e.Handled = true;
                NavigateFolderForward();
            }
        }

        // 폴더 뒤로 이동
        private void NavigateFolderBack()
        {
            if (_backStack.Count == 0 || _currentDirectory == null || _currentSvc == null)
                return;

            _suppressHistoryRecord = true;
            _forwardStack.Push((_currentDirectory, _currentSvc));

            var prev = _backStack.Pop();
            NavigateToFolder(prev.item, prev.svc, recordHistory: false);

            _suppressHistoryRecord = false;
        }

        // 폴더 앞으로 이동
        private void NavigateFolderForward()
        {
            if (_forwardStack.Count == 0 || _currentDirectory == null || _currentSvc == null)
                return;

            _suppressHistoryRecord = true;
            _backStack.Push((_currentDirectory, _currentSvc));

            var next = _forwardStack.Pop();
            NavigateToFolder(next.item, next.svc, recordHistory: false);

            _suppressHistoryRecord = false;
        }

        // 폴더 이동 공통 처리
        private void NavigateToFolder(ExplorerItem folder, FileSystemTreeServiceCore svc, bool recordHistory)
        {
            if (recordHistory && !_suppressHistoryRecord && _currentDirectory != null && _currentSvc != null)
            {
                _backStack.Push((_currentDirectory, _currentSvc));
                _forwardStack.Clear();
            }

            _currentDirectory = folder;
            _currentSvc = svc;

            PopulateRightPane(folder, svc);

            try
            {
                var node = svc.RevealFolderInTree(folder);
                if (node != null)
                {
                    node.IsExpanded = true;
                    FileTreeView.SelectedNodes.Clear();
                    FileTreeView.SelectedNodes.Add(node);
                }
            }
            catch { }

            SaveFilesystemState();
        }

        // TreeView 확장 이벤트 처리
        private void FileTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
        {
            if (args.Node?.Content is not ExplorerItem item) return;

            var svc = GetSvcForNode(args.Node);
            if (svc == null) return;

            if (item.Type == ExplorerItem.ExplorerItemType.Folder)
                item.IsFolderOpen = true;

            svc.EnsureChildrenLoaded(args.Node, item);

            RegisterChildren(args.Node, svc);

            if (item.Type == ExplorerItem.ExplorerItemType.Folder)
            {
                if (_currentDirectory?.FullPath != item.FullPath ||
                    _currentDirectory?.VolumeIndex != item.VolumeIndex ||
                    _currentSvc != svc)
                {
                    NavigateToFolder(item, svc, recordHistory: true);
                }
            }
        }

        // TreeView 접기 이벤트 처리
        private void FileTreeView_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
        {
            if (args.Node?.Content is ExplorerItem item &&
                item.Type == ExplorerItem.ExplorerItemType.Folder)
                item.IsFolderOpen = false;
        }

        // TreeView 항목 클릭 처리
        private void FileTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is not TreeViewNode node ||
                node.Content is not ExplorerItem item)
                return;

            var svc = GetSvcForNode(node);
            if (svc == null) return;

            if (item.Type == ExplorerItem.ExplorerItemType.Folder)
            {
                NavigateToFolder(item, svc, recordHistory: true);
            }
            else if (item.Type == ExplorerItem.ExplorerItemType.Partition)
            {
                var root = svc.MakeRootFolderItem(item);
                if (root != null)
                {
                    _itemSvcMap[root] = svc;
                    NavigateToFolder(root, svc, recordHistory: true);
                }
            }
        }

        // 오른쪽 리스트 클릭
        private void DirectoryListView_ItemClick(object sender, ItemClickEventArgs e) { }

        // 오른쪽 리스트 더블클릭 → 폴더 이동 또는 ADS 표시
        private void DirectoryListView_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is not FrameworkElement fe) return;
            if (fe.DataContext is not DirectoryEntryRow row) return;

            var svc = row.Svc;

            if (row.IsFolder)
            {
                NavigateToFolder(row.Item, svc, recordHistory: true);
                e.Handled = true;
                return;
            }

            if (IsAdsRow(row))
            {
                PopulateAdsRightPane(row.Item, svc);
                e.Handled = true;
                return;
            }

            try
            {
                var ntfs = svc.GetNtfs(row.Item.VolumeIndex);
                if (ntfs == null) return;

                var adsNames = NtfsMetadataHelper.ListNamedDataStreams(ntfs, row.Item.FullPath);
                if (adsNames.Count > 0)
                    PopulateAdsRightPane(row.Item, svc);
            }
            catch { }

            e.Handled = true;
        }

        // ADS 여부 판별
        private static bool IsAdsRow(DirectoryEntryRow row)
        {
            if (row.TypeText.Equals("ADS", StringComparison.OrdinalIgnoreCase))
                return true;

            var path = row.Item.FullPath ?? "";
            return path.Contains(':');
        }

        // 좌/우 패널 분할 드래그 조정
        private void PaneSplitterThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double dx = e.HorizontalChange;
            double newLeft = LeftPaneColumn.ActualWidth + dx;

            double minLeft = 220;
            double minRight = 220;
            double maxLeft = Math.Max(minLeft, RootLayout.ActualWidth - minRight);

            if (newLeft < minLeft) newLeft = minLeft;
            if (newLeft > maxLeft) newLeft = maxLeft;

            LeftPaneColumn.Width = new GridLength(newLeft, GridUnitType.Pixel);
        }

        // 오른쪽 패널: 1계층 폴더 내용 표시
        private void PopulateRightPane(ExplorerItem directoryItem, FileSystemTreeServiceCore svc)
        {
            _entries.Clear();

            var ntfs = svc.GetNtfs(directoryItem.VolumeIndex);
            if (ntfs == null)
            {
                RightHeaderTextBlock.Text = "NTFS 볼륨 없음";
                ResetRightViewState();
                return;
            }

            RightHeaderTextBlock.Text = $"{directoryItem.FullPath}";

            foreach (var child in svc.GetOneLevelEntries(directoryItem))
            {
                var info = _metaSvc.Parse(ntfs, child);

                _entries.Add(new DirectoryEntryRow
                {
                    Item = child,
                    Svc = svc,
                    Name = child.Name,
                    TypeText = child.Type == ExplorerItem.ExplorerItemType.Folder ? "Folder" : "File",
                    SizeText = child.Type == ExplorerItem.ExplorerItemType.Folder ? "" : $"{info.SizeBytes:N0} B",
                    CreateText = ToDisplayText(info.CreateUtc),
                    ModifyText = ToDisplayText(info.ModifyUtc),
                    AccessText = ToDisplayText(info.AccessUtc),
                    MftModifyText = ToDisplayText(info.MftModifyUtc),
                    MftEntryText = info.MftEntryNumber == 0 ? "" : info.MftEntryNumber.ToString(),
                    IsFolder = child.Type == ExplorerItem.ExplorerItemType.Folder
                });
            }

            ResetRightViewState();
        }

        // 오른쪽 패널: ADS 목록 표시
        private void PopulateAdsRightPane(ExplorerItem fileItem, FileSystemTreeServiceCore svc)
        {
            _entries.Clear();

            var ntfs = svc.GetNtfs(fileItem.VolumeIndex);
            if (ntfs == null)
            {
                RightHeaderTextBlock.Text = "NTFS 볼륨 없음";
                ResetRightViewState();
                return;
            }

            var basePath = fileItem.FullPath;
            var colonIdx = basePath.IndexOf(':');
            var rootPath = colonIdx >= 0 ? basePath.Substring(0, colonIdx) : basePath;

            var nameColonIdx = fileItem.Name.IndexOf(':');
            var rootName = nameColonIdx >= 0 ? fileItem.Name.Substring(0, nameColonIdx) : fileItem.Name;

            var adsNames = NtfsMetadataHelper.ListNamedDataStreams(ntfs, rootPath);

            if (adsNames.Count == 0)
            {
                var info0 = _metaSvc.Parse(ntfs, fileItem);
                RightHeaderTextBlock.Text = $"{rootPath}";

                _entries.Add(new DirectoryEntryRow
                {
                    Item = fileItem,
                    Svc = svc,
                    Name = fileItem.Name,
                    TypeText = "File",
                    SizeText = $"{info0.SizeBytes:N0} B",
                    CreateText = ToDisplayText(info0.CreateUtc),
                    ModifyText = ToDisplayText(info0.ModifyUtc),
                    AccessText = ToDisplayText(info0.AccessUtc),
                    MftModifyText = ToDisplayText(info0.MftModifyUtc),
                    MftEntryText = info0.MftEntryNumber == 0 ? "" : info0.MftEntryNumber.ToString(),
                    IsFolder = false
                });

                ResetRightViewState();
                return;
            }

            RightHeaderTextBlock.Text = $"{rootPath} (ADS)";

            foreach (var ads in adsNames)
            {
                var adsItem = new ExplorerItem
                {
                    Name = $"{rootName}:{ads}",
                    FullPath = $"{rootPath}:{ads}",
                    Type = ExplorerItem.ExplorerItemType.File,
                    VolumeIndex = fileItem.VolumeIndex
                };

                var info = _metaSvc.Parse(ntfs, adsItem);

                _entries.Add(new DirectoryEntryRow
                {
                    Item = adsItem,
                    Svc = svc,
                    Name = adsItem.Name,
                    TypeText = "ADS",
                    SizeText = $"{info.SizeBytes:N0} B",
                    CreateText = ToDisplayText(info.CreateUtc),
                    ModifyText = ToDisplayText(info.ModifyUtc),
                    AccessText = ToDisplayText(info.AccessUtc),
                    MftModifyText = ToDisplayText(info.MftModifyUtc),
                    MftEntryText = info.MftEntryNumber == 0 ? "" : info.MftEntryNumber.ToString(),
                    IsFolder = false
                });
            }

            ResetRightViewState();
        }

        // TableView 갱신 및 선택 초기화
        private void ResetRightViewState()
        {
            try
            {
                DirectoryTableView.SelectedItem = null;
                DirectoryTableView.SelectedIndex = -1;
                DirectoryTableView.SelectedItems?.Clear();
                DirectoryTableView.ItemsSource = null;
                DirectoryTableView.ItemsSource = _entries;
                DirectoryTableView.UpdateLayout();
            }
            catch { }
        }

        // UTC → 타임존 보정 문자열 변환
        private string ToDisplayText(DateTime dtUtc)
        {
            if (dtUtc == DateTime.MinValue) return "";

            var utc = dtUtc.Kind == DateTimeKind.Utc ? dtUtc : dtUtc.ToUniversalTime();
            if (_currentOffset == null) return utc.ToString("yyyy-MM-dd HH:mm:ss");

            var local = utc + _currentOffset.Value;
            return local.ToString("yyyy-MM-dd HH:mm:ss");
        }

        // "(UTC+09:00)" 형태에서 Offset 파싱
        private TimeSpan? ParseOffset(string? timezoneDisplay)
        {
            if (string.IsNullOrWhiteSpace(timezoneDisplay)) return null;

            string candidate = timezoneDisplay.Trim();
            int openIdx = candidate.IndexOf("(UTC", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                int closeIdx = candidate.IndexOf(')', openIdx);
                if (closeIdx > openIdx)
                    candidate = candidate.Substring(openIdx + 1, closeIdx - openIdx - 1);
            }

            if (!candidate.StartsWith("UTC", StringComparison.OrdinalIgnoreCase)) return null;
            candidate = candidate.Substring(3).Trim();

            int sign = 1;
            if (candidate.StartsWith("+")) candidate = candidate.Substring(1);
            else if (candidate.StartsWith("-"))
            {
                sign = -1;
                candidate = candidate.Substring(1);
            }

            var parts = candidate.Split(':');
            if (parts.Length == 0 || parts.Length > 2) return null;
            if (!int.TryParse(parts[0], out int hours)) return null;

            int minutes = 0;
            if (parts.Length == 2 && !int.TryParse(parts[1], out minutes)) return null;

            return new TimeSpan(sign * hours, sign * minutes, 0);
        }

        // SQLite에서 StaticImage path 리스트 가져오기
        private List<string> SelectEvidenceSourceStaticImagePaths(IntPtr db)
        {
            var list = new List<string>();

            NativeSqliteHelper.ExecCallback callback = (arg, columnCount, columnValues, columnNames) =>
            {
                var namePtrs = new IntPtr[columnCount];
                var valuePtrs = new IntPtr[columnCount];
                Marshal.Copy(columnNames, namePtrs, 0, columnCount);
                Marshal.Copy(columnValues, valuePtrs, 0, columnCount);

                for (int i = 0; i < columnCount; i++)
                {
                    string colName = NativeSqliteHelper.PtrToStringUtf8(namePtrs[i]) ?? "";
                    if (!colName.Equals("value", StringComparison.OrdinalIgnoreCase)) continue;

                    string colVal = valuePtrs[i] == IntPtr.Zero
                        ? ""
                        : (NativeSqliteHelper.PtrToStringUtf8(valuePtrs[i]) ?? "");

                    if (!string.IsNullOrWhiteSpace(colVal))
                        list.Add(colVal);
                }
                return 0;
            };

            IntPtr errPtr;
            int rc = NativeSqliteHelper.sqlite3_exec(
                db,
                "SELECT value FROM evidence_source WHERE type='StaticImage' ORDER BY id;",
                callback,
                IntPtr.Zero,
                out errPtr);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                if (errPtr != IntPtr.Zero)
                    NativeSqliteHelper.sqlite3_free(errPtr);
                return new List<string>();
            }

            return list;
        }

        // 오른쪽 클릭 → Extract 메뉴 표시
        private void DirectoryListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            if (e.OriginalSource is not FrameworkElement fe) return;
            if (fe.DataContext is not DirectoryEntryRow row) return;

            _contextRow = row;
            DirectoryTableView.SelectedItem = row;

            ExtractMenuItem.Text = row.IsFolder ? "Extract Folder" : "Extract File";
            ExtractMenuItem.IsEnabled = true;

            EntryContextFlyout.ShowAt(DirectoryTableView, e.GetPosition(DirectoryTableView));
            e.Handled = true;
        }

        // Extract 실행
        private async void ExtractMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var row = _contextRow;
            if (row == null) return;

            var svc = row.Svc;
            var ntfs = svc.GetNtfs(row.Item.VolumeIndex);
            if (ntfs == null) return;

            try
            {
                if (row.IsFolder)
                {
                    var pickedFolder = await PickDestinationFolderAsync();
                    if (pickedFolder == null) return;

                    var safeFolderName = SanitizeName(row.Item.Name);
                    if (string.IsNullOrWhiteSpace(safeFolderName))
                        safeFolderName = "ExtractedFolder";

                    var destRoot = await pickedFolder.CreateFolderAsync(
                        safeFolderName, CreationCollisionOption.OpenIfExists);

                    await ExtractHelper.ExtractDirectoryAsync(ntfs, row.Item.FullPath, destRoot);
                }
                else
                {
                    var safeFileName = SanitizeName(row.Item.Name);
                    if (string.IsNullOrWhiteSpace(safeFileName))
                        safeFileName = "unnamed.bin";

                    var destFile = await PickDestinationFileAsync(safeFileName);
                    if (destFile == null) return;

                    await ExtractHelper.ExtractFileAsync(ntfs, row.Item.FullPath, destFile);
                }
            }
            catch { }
            finally { _contextRow = null; }
        }

        // 현재 Window 핸들 반환
        private IntPtr GetHwnd()
        {
            var window = App.MainWindowInstance;
            return window != null ? WindowNative.GetWindowHandle(window) : IntPtr.Zero;
        }

        // 폴더 저장 위치 선택
        private async Task<StorageFolder?> PickDestinationFolderAsync()
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Desktop };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, GetHwnd());
            return await picker.PickSingleFolderAsync();
        }

        // 파일 저장 위치 선택
        private async Task<StorageFile?> PickDestinationFileAsync(string suggestedName)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.Desktop,
                SuggestedFileName = suggestedName
            };

            var ext = Path.GetExtension(suggestedName);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".bin";

            picker.FileTypeChoices.Add("All files", new List<string> { ext });
            InitializeWithWindow.Initialize(picker, GetHwnd());
            return await picker.PickSaveFileAsync();
        }

        // 파일/폴더 이름 유효 문자 정리
        private static string SanitizeName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            name = name.Replace(':', '_');
            return name.Trim();
        }

        // 상태 메시지 UI 제거 → no-op
        private void Status(string msg) { }
    }
}
