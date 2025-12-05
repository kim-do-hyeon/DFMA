using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Streams;

using WinUiApp.Interop;
using WinUiApp.Pages.ArtifactsAnalysis;

namespace WinUiApp.Services
{
    /// E01 내부 NTFS를 열어 특정 경로 이하 파일을 스캔/추출하는 서비스 클래스
    public static class CaseFilesystemScanService
    {
        // libewf DLL의 상대 경로 상수
        private const string EwfDllRelPath = @"dll\EwfTools\libewf.dll";

        // 스캔 결과 한 항목(파일/디렉터리)의 전체 경로와 디렉터리 여부를 나타내는 레코드
        public record FsScanEntry(string FullPath, bool IsDirectory);

        // 단일 이미지/볼륨 스캔 결과를 담는 레코드
        public record FsScanResult(
            string ImagePath,
            int VolumeIndex,
            string RootPath,
            IReadOnlyList<FsScanEntry> Directories,
            IReadOnlyList<FsScanEntry> Files
        );

        /// <summary>
        /// 현재 로드된 케이스의 StaticImage(E01) 전체에 대해 스캔 수행.
        /// </summary>
        // 현재 케이스의 모든 StaticImage(E01)를 대상으로 지정 NTFS 경로를 스캔하는 진입점
        public static IReadOnlyList<FsScanResult> ScanCurrentCaseStaticImages(
            string rootNtfsPath,
            IEnumerable<string>? includeFileNames = null,
            IEnumerable<string>? includeExtensions = null,
            int? volumeIndex = null)
        {
            var images = GetStaticImagePathsFromCurrentCase();
            var results = new List<FsScanResult>();

            foreach (var img in images)
            {
                results.AddRange(
                    ScanImage(img, rootNtfsPath, includeFileNames, includeExtensions, volumeIndex)
                );
            }

            return results;
        }

        /// <summary>
        /// 단일 E01 이미지에 대해 스캔 수행.
        /// volumeIndex가 null이면 이미지 내 모든 NTFS 파티션 스캔.
        /// </summary>
        // 하나의 E01 파일을 열어 특정 볼륨(또는 전체)에 대해 파일 시스템을 스캔하는 함수
        public static IReadOnlyList<FsScanResult> ScanImage(
            string e01Path,
            string rootNtfsPath,
            IEnumerable<string>? includeFileNames = null,
            IEnumerable<string>? includeExtensions = null,
            int? volumeIndex = null)
        {
            if (string.IsNullOrWhiteSpace(e01Path))
                throw new ArgumentNullException(nameof(e01Path));
            if (!File.Exists(e01Path))
                throw new FileNotFoundException("E01 파일을 찾을 수 없습니다.", e01Path);

            rootNtfsPath = NormalizeNtfsPath(rootNtfsPath);

            var includeNamesSet = ToLowerSet(includeFileNames);
            var includeExtSet = ToLowerSet(
                includeExtensions?.Select(e => e.StartsWith(".") ? e : "." + e)
            );

            using var session = OpenNtfsVolumes(e01Path);

            var targets = volumeIndex.HasValue
                ? session.Volumes
                    .Select((v, idx) => (v, idx))
                    .Where(t => t.idx == volumeIndex.Value)
                : session.Volumes.Select((v, idx) => (v, idx));

            var results = new List<FsScanResult>();

            foreach (var (ntfs, vIdx) in targets)
            {
                var dirs = new List<FsScanEntry>();
                var files = new List<FsScanEntry>();

                ScanRecursive(
                    ntfs,
                    rootNtfsPath,
                    includeNamesSet,
                    includeExtSet,
                    dirs,
                    files
                );

                results.Add(new FsScanResult(
                    ImagePath: e01Path,
                    VolumeIndex: vIdx,
                    RootPath: rootNtfsPath,
                    Directories: dirs,
                    Files: files
                ));
            }

            return results;
        }

        /// <summary>
        /// 이미 열려있는 NtfsFileSystem에 대해 스캔 수행(외부에서 Ntfs를 이미 가지고 있을 때).
        /// </summary>
        // 외부에서 생성한 NtfsFileSystem 인스턴스를 활용해 지정 경로를 스캔하는 함수
        public static FsScanResult ScanNtfs(
            NtfsFileSystem ntfs,
            string imagePathLabel,
            int volumeIndex,
            string rootNtfsPath,
            IEnumerable<string>? includeFileNames = null,
            IEnumerable<string>? includeExtensions = null)
        {
            if (ntfs == null) throw new ArgumentNullException(nameof(ntfs));

            rootNtfsPath = NormalizeNtfsPath(rootNtfsPath);

            var includeNamesSet = ToLowerSet(includeFileNames);
            var includeExtSet = ToLowerSet(
                includeExtensions?.Select(e => e.StartsWith(".") ? e : "." + e)
            );

            var dirs = new List<FsScanEntry>();
            var files = new List<FsScanEntry>();

            ScanRecursive(ntfs, rootNtfsPath, includeNamesSet, includeExtSet, dirs, files);

            return new FsScanResult(imagePathLabel, volumeIndex, rootNtfsPath, dirs, files);
        }

        /// <summary>
        /// ScanCurrentCaseStaticImages/ScanImage 결과를
        /// [CaseRoot]\Extract\[ImageName]\[customFolderName]\... 구조로 추출.
        /// </summary>
        /// <param name="scanResults">Scan 결과</param>
        /// <param name="customFolderName">사용자 정의 폴더명 (예: "RegistryHives")</param>
        // 스캔 결과에 포함된 파일들을 케이스 루트의 Extract 구조로 실제 파일로 추출하는 비동기 함수
        public static async Task ExtractScanResultsAsync(
            IEnumerable<FsScanResult> scanResults,
            string customFolderName,
            bool keepNtfsStructure = true)
        {
            if (scanResults == null) throw new ArgumentNullException(nameof(scanResults));
            if (string.IsNullOrWhiteSpace(customFolderName))
                customFolderName = "ExtractedFiles";

            var caseRoot = CaseImformation.CurrentCaseRoot;
            if (string.IsNullOrEmpty(caseRoot) || !Directory.Exists(caseRoot))
                throw new DirectoryNotFoundException("CurrentCaseRoot가 비어있거나 존재하지 않습니다.");

            var extractRoot = Path.Combine(caseRoot, "Extract");
            Directory.CreateDirectory(extractRoot);

            foreach (var r in scanResults)
            {
                var imageName = Path.GetFileNameWithoutExtension(r.ImagePath);
                if (string.IsNullOrWhiteSpace(imageName))
                    imageName = "UnknownImage";

                var imageFolder = Path.Combine(extractRoot, imageName);
                Directory.CreateDirectory(imageFolder);

                var userFolder = Path.Combine(imageFolder, customFolderName);
                Directory.CreateDirectory(userFolder);

                using var session = OpenNtfsVolumes(r.ImagePath);

                if (r.VolumeIndex < 0 || r.VolumeIndex >= session.Volumes.Count)
                    continue;

                var ntfs = session.Volumes[r.VolumeIndex];

                foreach (var f in r.Files)
                {
                    await ExtractOneFileAsync(ntfs, f.FullPath, userFolder, keepNtfsStructure);
                }
            }
        }

        // NTFS 상의 단일 파일을 실제 디스크로 복사(추출)하는 비동기 함수
        private static async Task ExtractOneFileAsync(
            NtfsFileSystem ntfs,
            string ntfsFilePath,
            string userFolderRoot,
            bool keepNtfsStructure)
        {
            string rel;

            if (keepNtfsStructure)
            {
                // ✅ 기존 방식: NTFS 경로 구조 유지
                rel = ntfsFilePath.TrimStart('\\').Replace('/', '\\');
            }
            else
            {
                // ✅ 새 방식: 파일명만 사용 (경로 제거)
                rel = Path.GetFileName(ntfsFilePath);
            }

            rel = SanitizeRelativePath(rel);

            var destFullPath = Path.Combine(userFolderRoot, rel);

            var destDir = Path.GetDirectoryName(destFullPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            destFullPath = EnsureUniquePath(destFullPath);

            using var src = ntfs.OpenFile(ntfsFilePath, FileMode.Open, FileAccess.Read);
            using var dst = File.Open(destFullPath, FileMode.Create, FileAccess.Write);
            await src.CopyToAsync(dst);
        }

        // 대상 경로에 파일이 있으면 (1), (2)... 식으로 유일한 새 파일명을 만들어주는 헬퍼
        private static string EnsureUniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            var dir = Path.GetDirectoryName(path)!;
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);

            int i = 1;
            while (true)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
                i++;
            }
        }

        // 지정 경로부터 하위 디렉터리를 재귀적으로 순회하며 조건에 맞는 파일/폴더를 수집
        private static void ScanRecursive(
            NtfsFileSystem ntfs,
            string currentPath,
            HashSet<string>? includeNames,
            HashSet<string>? includeExts,
            List<FsScanEntry> dirsOut,
            List<FsScanEntry> filesOut)
        {
            IEnumerable<string> childDirs = Enumerable.Empty<string>();
            try
            {
                childDirs = ntfs.GetDirectories(currentPath)
                                .Where(p => IsDirectChild(currentPath, p));
            }
            catch { }

            foreach (var dirPath in childDirs)
            {
                dirsOut.Add(new FsScanEntry(dirPath, true));
                ScanRecursive(ntfs, dirPath, includeNames, includeExts, dirsOut, filesOut);
            }

            IEnumerable<string> childFiles = Enumerable.Empty<string>();
            try
            {
                childFiles = ntfs.GetFiles(currentPath)
                                 .Where(p => IsDirectChild(currentPath, p));
            }
            catch { }

            foreach (var filePath in childFiles)
            {
                var name = GetLastPathComponent(filePath);
                var ext = Path.GetExtension(name).ToLowerInvariant();

                if (ShouldInclude(name, ext, includeNames, includeExts))
                    filesOut.Add(new FsScanEntry(filePath, false));
            }
        }

        // 파일 이름/확장자가 필터 조건에 맞는지 판단하는 함수
        private static bool ShouldInclude(
            string fileName,
            string extLower,
            HashSet<string>? includeNamesLower,
            HashSet<string>? includeExtsLower)
        {
            if ((includeNamesLower == null || includeNamesLower.Count == 0) &&
                (includeExtsLower == null || includeExtsLower.Count == 0))
                return true;

            if (includeNamesLower != null && includeNamesLower.Contains(fileName.ToLowerInvariant()))
                return true;

            if (includeExtsLower != null && includeExtsLower.Contains(extLower))
                return true;

            return false;
        }

        // 한 번 열린 E01/볼륨/NTFS 파일 시스템을 묶어서 관리하는 세션 객체
        private sealed class ImageSession : IDisposable
        {
            // libewf 핸들을 나타내는 래퍼
            public EwfLibraryHandle? LibHandle;
            // EWF 이미지를 스트림으로 다루기 위한 객체
            public EwfStream? EwfStream;
            // Raw 디스크 추상화 객체
            public VirtualDisk? Disk;
            // 디스크에서 파티션/볼륨을 관리하는 매니저
            public VolumeManager? VolumeManager;
            // 논리 볼륨별 NtfsFileSystem 리스트
            public List<NtfsFileSystem> Volumes = new();

            // 세션 내 모든 리소스를 정리(dispose)하는 메서드
            public void Dispose()
            {
                foreach (var v in Volumes) v.Dispose();
                Volumes.Clear();

                VolumeManager = null;
                Disk?.Dispose(); Disk = null;
                EwfStream?.Dispose(); EwfStream = null;
                LibHandle?.Dispose(); LibHandle = null;
            }
        }

        // E01 파일을 열어 NTFS 볼륨 목록을 준비하는 세션을 생성하는 함수
        private static ImageSession OpenNtfsVolumes(string e01Path)
        {
            Try(() => DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(NtfsFileSystem).Assembly));

            var session = new ImageSession();

            var ewfFull = Path.Combine(AppContext.BaseDirectory, EwfDllRelPath);
            if (!File.Exists(ewfFull))
                throw new FileNotFoundException("libewf.dll을 찾을 수 없습니다.", ewfFull);

            session.LibHandle = EwfNativeLibraryLoader.Load(ewfFull);

            var handle = EwfNativeAdvanced.HandleInit(session.LibHandle);
            EwfNativeAdvanced.HandleOpenWide(session.LibHandle, handle, e01Path);
            var mediaSize = EwfNativeAdvanced.GetMediaSize(session.LibHandle, handle);

            session.EwfStream = new EwfStream(session.LibHandle, handle, mediaSize);
            session.Disk = new DiscUtils.Raw.Disk(session.EwfStream, Ownership.None);
            session.VolumeManager = new VolumeManager(session.Disk);

            foreach (var vol in session.VolumeManager.GetLogicalVolumes())
            {
                var volStream = vol.Open();
                try
                {
                    var ntfs = new NtfsFileSystem(volStream);
                    ntfs.NtfsOptions.HideHiddenFiles = false;
                    ntfs.NtfsOptions.HideSystemFiles = false;
                    Try(() => ntfs.NtfsOptions.HideMetafiles = false);

                    session.Volumes.Add(ntfs);
                }
                catch
                {
                    volStream.Dispose();
                }
            }

            return session;
        }

        // 현재 케이스 DB에서 StaticImage 타입의 이미지 경로 목록을 조회하는 함수
        public static List<string> GetStaticImagePathsFromCurrentCase()
        {
            var caseRoot = CaseImformation.CurrentCaseRoot;
            var dbPath = CaseImformation.CurrentDbPath;

            if (string.IsNullOrEmpty(caseRoot) || !Directory.Exists(caseRoot))
                return new List<string>();

            if (string.IsNullOrEmpty(dbPath))
                dbPath = Path.Combine(caseRoot, "DFMA-Case.dfmadb");

            if (!File.Exists(dbPath))
                return new List<string>();

            // Microsoft.Data.Sqlite가 자동으로 네이티브 DLL을 관리합니다.
            int rc = NativeSqliteHelper.sqlite3_open_v2(
                dbPath, out var db,
                NativeSqliteHelper.SQLITE_OPEN_READWRITE, null);

            if (rc != NativeSqliteHelper.SQLITE_OK || db == IntPtr.Zero)
                return new List<string>();

            try
            {
                return SelectEvidenceSourceStaticImagePaths(db);
            }
            finally
            {
                NativeSqliteHelper.sqlite3_close(db);
            }
        }

        // evidence_source 테이블에서 StaticImage 타입의 value 컬럼만 추출하는 함수
        private static List<string> SelectEvidenceSourceStaticImagePaths(IntPtr db)
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

            int rc = NativeSqliteHelper.sqlite3_exec(
                db,
                "SELECT value FROM evidence_source WHERE type='StaticImage' ORDER BY id;",
                callback,
                IntPtr.Zero,
                out var errPtr);

            if (rc != NativeSqliteHelper.SQLITE_OK)
            {
                if (errPtr != IntPtr.Zero)
                    NativeSqliteHelper.sqlite3_free(errPtr);
                return new List<string>();
            }

            return list;
        }

        // NTFS 경로 문자열을 \ 기준 및 앞/뒤 슬래시 처리 등 표준 형태로 정규화하는 함수
        private static string NormalizeNtfsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "\\";
            path = path.Replace('/', '\\');
            if (!path.StartsWith("\\")) path = "\\" + path;
            if (path.Length > 1 && path.EndsWith("\\")) path = path.TrimEnd('\\');
            return path;
        }

        // 문자열 컬렉션을 소문자/트림 후 HashSet으로 변환하는 유틸
        private static HashSet<string>? ToLowerSet(IEnumerable<string>? items)
        {
            if (items == null) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                if (!string.IsNullOrWhiteSpace(it))
                    set.Add(it.Trim().ToLowerInvariant());
            }
            return set;
        }

        // childPath가 parentPath의 바로 아래 자식인지(중간에 서브폴더 없는 경우) 판단하는 함수
        private static bool IsDirectChild(string parentPath, string childPath)
        {
            if (string.IsNullOrEmpty(parentPath)) parentPath = "\\";
            if (!childPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase))
                return false;

            var remainder = childPath.Substring(parentPath.Length).Trim('\\');
            return remainder.Length > 0 && !remainder.Contains('\\');
        }

        // 전체 경로 문자열에서 마지막 컴포넌트(파일/디렉터리 이름)만 추출
        private static string GetLastPathComponent(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return fullPath;

            int idx = fullPath.LastIndexOf('\\');
            return idx >= 0 ? fullPath[(idx + 1)..] : fullPath;
        }

        /// <summary>
        /// Windows 파일시스템에서 금지된 문자/ADS ':' 등을 안전한 문자로 치환.
        /// 디렉터리 구분자는 유지.
        /// </summary>
        // 상대 경로 내의 파일/폴더 이름에서 사용 불가 문자를 안전한 문자로 바꾸는 함수
        private static string SanitizeRelativePath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return rel;

            var invalidNameChars = Path.GetInvalidFileNameChars();
            var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i];

                p = p.Replace(':', '_');

                foreach (var ch in invalidNameChars)
                {
                    if (ch == '\\') continue;
                    p = p.Replace(ch, '_');
                }

                parts[i] = p;
            }

            return string.Join('\\', parts);
        }

        // 예외를 무시하고 액션을 실행하는 단순 래퍼
        private static void Try(Action a)
        {
            try { a(); } catch { }
        }
    }
}
