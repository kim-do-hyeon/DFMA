using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Streams;

#nullable enable

namespace WinUiApp
{
    // 탐색기 항목(디스크/파티션/폴더/파일) 상태 및 아이콘 정보 보유 모델
    public class ExplorerItem : INotifyPropertyChanged
    {
        public enum ExplorerItemType { DiskImage, Partition, Folder, File }

        string _name = "", _fullPath = "";
        ExplorerItemType _type;
        int _volumeIndex = -1;
        bool _hasLoadedChildren, _isFolderOpen;

        public string Name { get => _name; set => Set(ref _name, value); }
        public string FullPath { get => _fullPath; set => Set(ref _fullPath, value); }

        public ExplorerItemType Type
        {
            get => _type;
            set => Set(ref _type, value, notifyIcon: true);
        }

        public int VolumeIndex { get => _volumeIndex; set => Set(ref _volumeIndex, value); }
        public bool HasLoadedChildren { get => _hasLoadedChildren; set => Set(ref _hasLoadedChildren, value); }

        public bool IsFolderOpen
        {
            get => _isFolderOpen;
            set => Set(ref _isFolderOpen, value, notifyIcon: Type == ExplorerItemType.Folder);
        }

        public string IconGlyph => Type switch
        {
            ExplorerItemType.DiskImage => "\uE74E",
            ExplorerItemType.Partition => "\uEDA2",
            ExplorerItemType.Folder => IsFolderOpen ? "\uE838" : "\uE8B7",
            ExplorerItemType.File => "\uE8A5",
            _ => "\uE8A5"
        };

        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        // 필드 값을 설정하고 PropertyChanged를 발생시키는 유틸리티
        bool Set<T>(ref T field, T value, bool notifyIcon = false, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(n);
            if (notifyIcon) OnPropertyChanged(nameof(IconGlyph));
            return true;
        }
    }
}

namespace WinUiApp.Services
{
    // NTFS 기반 디렉터리 트리 구조를 구성하고 TreeView를 관리하는 핵심 서비스
    public sealed class FileSystemTreeServiceCore : IDisposable
    {
        const string EwfDllRelPath = @"dll\EwfTools\libewf.dll";

        EwfLibraryHandle? _libHandle;
        EwfStream? _ewfStream;

        VirtualDisk? _disk;
        VolumeManager? _volumeManager;

        readonly List<NtfsFileSystem> _ntfsVolumes = new();
        readonly TreeView _treeView;
        readonly Action<string> _status;

        readonly HashSet<TreeViewNode> _loadingNodes = new();

        // 서비스 초기화 및 DiscUtils 준비
        public FileSystemTreeServiceCore(TreeView treeView, Action<string> statusCallback)
        {
            _treeView = treeView ?? throw new ArgumentNullException(nameof(treeView));
            _status = statusCallback ?? (_ => { });

            Try(() => DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(NtfsFileSystem).Assembly));
        }

        // 현재 로드된 이미지 자원 해제
        public void Dispose() => DisposeCurrentImage();

        // 특정 볼륨의 NTFS 객체 반환
        public NtfsFileSystem? GetNtfs(int idx)
        {
            if (idx < 0 || idx >= _ntfsVolumes.Count) return null;
            return _ntfsVolumes[idx];
        }

        // 디스크 이미지/볼륨/NTFS 세션 해제 및 TreeView 초기화
        public void DisposeCurrentImage()
        {
            foreach (var n in _ntfsVolumes) n.Dispose();
            _ntfsVolumes.Clear();

            _volumeManager = null;
            _disk?.Dispose(); _disk = null;
            _ewfStream?.Dispose(); _ewfStream = null;
            _libHandle?.Dispose(); _libHandle = null;

            _treeView.RootNodes.Clear();
            _loadingNodes.Clear();
        }

        // E01 디스크 이미지를 로드하여 TreeView에 최상위 노드 생성
        public void LoadDiskImageRoot(string e01Path)
        {
            if (string.IsNullOrWhiteSpace(e01Path))
                throw new ArgumentNullException(nameof(e01Path));
            if (!File.Exists(e01Path))
                throw new FileNotFoundException("E01 파일을 찾을 수 없습니다.", e01Path);

            DisposeCurrentImage();

            var ewfFull = Path.Combine(AppContext.BaseDirectory, EwfDllRelPath);
            if (!File.Exists(ewfFull))
                throw new FileNotFoundException("libewf.dll을 찾을 수 없습니다.", ewfFull);

            _libHandle = EwfNativeLibraryLoader.Load(ewfFull);

            var handle = EwfNativeAdvanced.HandleInit(_libHandle);
            EwfNativeAdvanced.HandleOpenWide(_libHandle, handle, e01Path);
            var mediaSize = EwfNativeAdvanced.GetMediaSize(_libHandle, handle);

            _ewfStream = new EwfStream(_libHandle, handle, mediaSize);
            _disk = new DiscUtils.Raw.Disk(_ewfStream, Ownership.None);
            _volumeManager = new VolumeManager(_disk);

            _treeView.RootNodes.Clear();

            var diskSizeMiB = (long)(_disk.Capacity / 1048576L);
            var fileName = Path.GetFileName(e01Path);

            var diskItem = new ExplorerItem
            {
                Name = $"{fileName} ({diskSizeMiB} MiB)",
                Type = ExplorerItem.ExplorerItemType.DiskImage
            };

            _treeView.RootNodes.Add(NewNode(diskItem, unrealized: true));
        }

        // TreeViewNode의 자식 노드를 필요 시 로드
        public void EnsureChildrenLoaded(TreeViewNode node, ExplorerItem item)
        {
            if (item.HasLoadedChildren)
            {
                node.HasUnrealizedChildren = false;
                return;
            }

            if (!_loadingNodes.Add(node))
                return;

            try
            {
                switch (item.Type)
                {
                    case ExplorerItem.ExplorerItemType.DiskImage:
                        LoadPartitions(node);
                        break;
                    case ExplorerItem.ExplorerItemType.Partition:
                        EnsureRootFolderNode(node, item);
                        break;
                    case ExplorerItem.ExplorerItemType.Folder:
                        LoadDirectoryTreeOnly(node, item);
                        break;
                }

                item.HasLoadedChildren = true;
                node.HasUnrealizedChildren = false;
            }
            finally
            {
                _loadingNodes.Remove(node);
            }
        }

        // NTFS 파티션 목록을 로드하여 자식 노드로 추가
        void LoadPartitions(TreeViewNode diskNode)
        {
            if (_volumeManager is null || _disk is null) return;

            diskNode.Children.Clear();

            int partIndex = 0;
            foreach (var vol in _volumeManager.GetLogicalVolumes())
            {
                partIndex++;
                var sizeMiB = (long)(vol.Length / 1048576L);

                var baseName = $"Partition {partIndex} ({sizeMiB} MiB)";
                var volStream = vol.Open();

                try
                {
                    var ntfs = new NtfsFileSystem(volStream);
                    ntfs.NtfsOptions.HideHiddenFiles = false;
                    ntfs.NtfsOptions.HideSystemFiles = false;
                    Try(() => ntfs.NtfsOptions.HideMetafiles = false);

                    _ntfsVolumes.Add(ntfs);
                    var vIdx = _ntfsVolumes.Count - 1;

                    var label = "";
                    Try(() => label = ntfs.VolumeLabel);

                    var displayName = string.IsNullOrEmpty(label)
                        ? $"{baseName} [NTFS]"
                        : $"{baseName} [NTFS] ({label})";

                    diskNode.Children.Add(NewNode(
                        new ExplorerItem
                        {
                            Name = displayName,
                            Type = ExplorerItem.ExplorerItemType.Partition,
                            VolumeIndex = vIdx
                        },
                        unrealized: true
                    ));
                }
                catch
                {
                    volStream.Dispose();
                    diskNode.Children.Add(NewNode(
                        new ExplorerItem
                        {
                            Name = $"{baseName} [Unknown]",
                            Type = ExplorerItem.ExplorerItemType.Partition,
                            VolumeIndex = -1,
                            HasLoadedChildren = true
                        },
                        unrealized: false
                    ));
                }
            }

            if (diskNode.Children.Count == 0)
                _status("논리 볼륨을 찾지 못했습니다.");
        }

        // 파티션 루트(\) 폴더 노드를 생성
        void EnsureRootFolderNode(TreeViewNode partNode, ExplorerItem partItem)
        {
            partNode.Children.Clear();
            if (GetNtfs(partItem.VolumeIndex) == null) return;

            var rootItem = new ExplorerItem
            {
                Name = "\\",
                FullPath = "\\",
                Type = ExplorerItem.ExplorerItemType.Folder,
                VolumeIndex = partItem.VolumeIndex
            };

            partNode.Children.Add(NewNode(rootItem, unrealized: true));
        }

        // 왼쪽 트리에 폴더 하위 디렉터리만 로드
        void LoadDirectoryTreeOnly(TreeViewNode parentNode, ExplorerItem parentItem)
        {
            var ntfs = GetNtfs(parentItem.VolumeIndex);
            if (ntfs == null) return;

            var path = string.IsNullOrEmpty(parentItem.FullPath) ? "\\" : parentItem.FullPath;
            parentNode.Children.Clear();

            try
            {
                foreach (var dirPath in ntfs.GetDirectories(path).Where(p => IsDirectChild(path, p)))
                {
                    parentNode.Children.Add(NewNode(
                        new ExplorerItem
                        {
                            Name = GetLastPathComponent(dirPath),
                            FullPath = dirPath,
                            Type = ExplorerItem.ExplorerItemType.Folder,
                            VolumeIndex = parentItem.VolumeIndex
                        },
                        unrealized: true
                    ));
                }
            }
            catch (Exception ex)
            {
                parentNode.Children.Add(NewNode(
                    new ExplorerItem
                    {
                        Name = $"<오류: {ex.Message}>",
                        FullPath = path,
                        Type = ExplorerItem.ExplorerItemType.Folder,
                        VolumeIndex = parentItem.VolumeIndex,
                        HasLoadedChildren = true
                    },
                    unrealized: false
                ));
            }
        }

        // 오른쪽 리스트에 표시할 폴더의 직계 항목(파일/폴더) 1계층을 반환
        public List<ExplorerItem> GetOneLevelEntries(ExplorerItem folderItem)
        {
            var list = new List<ExplorerItem>();
            var ntfs = GetNtfs(folderItem.VolumeIndex);
            if (ntfs == null) return list;

            var path = string.IsNullOrEmpty(folderItem.FullPath) ? "\\" : folderItem.FullPath;

            try
            {
                foreach (var d in ntfs.GetDirectories(path).Where(p => IsDirectChild(path, p)))
                {
                    list.Add(new ExplorerItem
                    {
                        Name = GetLastPathComponent(d),
                        FullPath = d,
                        Type = ExplorerItem.ExplorerItemType.Folder,
                        VolumeIndex = folderItem.VolumeIndex
                    });
                }

                foreach (var f in ntfs.GetFiles(path).Where(p => IsDirectChild(path, p)))
                {
                    list.Add(new ExplorerItem
                    {
                        Name = GetLastPathComponent(f),
                        FullPath = f,
                        Type = ExplorerItem.ExplorerItemType.File,
                        VolumeIndex = folderItem.VolumeIndex
                    });
                }
            }
            catch { }

            return list
                .OrderBy(x => x.Type == ExplorerItem.ExplorerItemType.File)
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 파티션 클릭 시 루트(\) ExplorerItem 생성
        public ExplorerItem? MakeRootFolderItem(ExplorerItem partitionItem)
        {
            if (partitionItem.Type != ExplorerItem.ExplorerItemType.Partition) return null;
            if (GetNtfs(partitionItem.VolumeIndex) == null) return null;

            return new ExplorerItem
            {
                Name = "\\",
                FullPath = "\\",
                Type = ExplorerItem.ExplorerItemType.Folder,
                VolumeIndex = partitionItem.VolumeIndex
            };
        }

        // 오른쪽에서 선택한 폴더를 기준으로 왼쪽 TreeView 트리를 따라가며 자동 확장
        public TreeViewNode? RevealFolderInTree(ExplorerItem folderItem)
        {
            if (folderItem.Type != ExplorerItem.ExplorerItemType.Folder) return null;

            // 1) 해당 볼륨 파티션 노드 찾기
            var partNode = FindNodeByPredicate(n =>
                n.Content is ExplorerItem ei &&
                ei.Type == ExplorerItem.ExplorerItemType.Partition &&
                ei.VolumeIndex == folderItem.VolumeIndex);

            if (partNode == null) return null;

            EnsureChildrenLoaded(partNode, (ExplorerItem)partNode.Content);
            partNode.IsExpanded = true;

            // 2) Partition 아래 Root("\") 노드 찾기
            var rootNode = partNode.Children.FirstOrDefault(c =>
                c.Content is ExplorerItem ei &&
                ei.Type == ExplorerItem.ExplorerItemType.Folder &&
                (ei.FullPath == "\\" || string.IsNullOrEmpty(ei.FullPath)));

            if (rootNode == null) return null;

            EnsureChildrenLoaded(rootNode, (ExplorerItem)rootNode.Content);
            rootNode.IsExpanded = true;

            var targetPath = folderItem.FullPath.Trim('\\');
            if (string.IsNullOrEmpty(targetPath))
                return rootNode;

            // 3) 경로 세그먼트 순차 탐색하며 펼침
            var segments = targetPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            var current = rootNode;
            var running = "";

            foreach (var seg in segments)
            {
                running = running == "" ? "\\" + seg : running + "\\" + seg;

                EnsureChildrenLoaded(current, (ExplorerItem)current.Content);

                var next = current.Children.FirstOrDefault(c =>
                    c.Content is ExplorerItem ei &&
                    ei.Type == ExplorerItem.ExplorerItemType.Folder &&
                    ei.FullPath.Equals(running, StringComparison.OrdinalIgnoreCase));

                if (next == null) break;

                next.IsExpanded = true;
                current = next;
            }

            return current;
        }

        // 트리 내부에서 탐색 조건을 만족하는 노드를 찾는 유틸리티
        TreeViewNode? FindNodeByPredicate(Func<TreeViewNode, bool> pred)
        {
            foreach (var r in _treeView.RootNodes)
            {
                var found = FindRec(r, pred);
                if (found != null) return found;
            }
            return null;

            static TreeViewNode? FindRec(TreeViewNode n, Func<TreeViewNode, bool> pred)
            {
                if (pred(n)) return n;
                foreach (var ch in n.Children)
                {
                    var f = FindRec(ch, pred);
                    if (f != null) return f;
                }
                return null;
            }
        }

        // ExplorerItem을 TreeViewNode로 래핑하여 생성
        static TreeViewNode NewNode(ExplorerItem item, bool unrealized) =>
            new TreeViewNode { Content = item, HasUnrealizedChildren = unrealized };

        // childPath가 parentPath의 직계 폴더인지 확인
        static bool IsDirectChild(string parentPath, string childPath)
        {
            if (string.IsNullOrEmpty(parentPath)) parentPath = "\\";
            if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase))
                return false;

            var remainder = childPath.Substring(parentPath.Length).Trim('\\');
            return remainder.Length > 0 && !remainder.Contains('\\');
        }

        // 경로에서 마지막 폴더/파일명 추출
        static string GetLastPathComponent(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            var idx = fullPath.LastIndexOf('\\');
            return idx < 0 ? fullPath : fullPath[(idx + 1)..];
        }

        // 예외를 무시하고 실행하는 안전 호출
        static void Try(Action a) { try { a(); } catch { } }
    }
}
