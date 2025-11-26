using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUiApp.Interop
{
    // DLL 로드 관리하는 헬퍼.
    public static class NativeDllManager
    {
        // 이미 로드한 DLL 핸들을 캐싱 (중복 로드 방지)
        private static readonly ConcurrentDictionary<string, IntPtr> _loadedLibraries =
            new(StringComparer.OrdinalIgnoreCase);

        // 네이티브 DLL을 로드하고, 실제 로드된 DLL의 절대 경로를 반환.
        public static string LoadNativeLibrary(string libraryNameOrPath, string? searchSubDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(libraryNameOrPath))
                throw new ArgumentException("libraryNameOrPath 는 비어 있을 수 없습니다.", nameof(libraryNameOrPath));

            // 1) 경로가 포함된 경우(절대/상대): 그대로 그 경로를 기준으로 처리
            if (Path.IsPathRooted(libraryNameOrPath) ||
                libraryNameOrPath.Contains(Path.DirectorySeparatorChar) ||
                libraryNameOrPath.Contains(Path.AltDirectorySeparatorChar))
            {
                var fullPath = Path.GetFullPath(libraryNameOrPath);
                if (!File.Exists(fullPath))
                {
                    // 지정한 경로에 DLL이 없다면 바로 예외
                    throw new NativeDllLoadException(
                        Path.GetFileName(libraryNameOrPath),
                        new[] { fullPath });
                }

                return LoadFromFullPathInternal(fullPath);
            }

            // 2) 이름만 넘어온 경우: 여러 후보 경로를 만들어서 순차 검색
            var probedPaths = new List<string>();
            var baseDir = AppContext.BaseDirectory;
            var candidates = new List<string>();

            // 내부 헬퍼: 검색 후보 경로 추가
            void AddCandidate(string dir)
            {
                if (string.IsNullOrWhiteSpace(dir))
                    return;

                candidates.Add(Path.Combine(dir, libraryNameOrPath));
            }

            // (1) 호출 시 지정한 서브 폴더가 있다면 최우선으로 검색
            if (!string.IsNullOrWhiteSpace(searchSubDirectory))
            {
                AddCandidate(Path.Combine(baseDir, searchSubDirectory));
            }

            // (2) 실행 파일 폴더
            AddCandidate(baseDir);

            // (3) 실행 파일 폴더/dll
            AddCandidate(Path.Combine(baseDir, "dll"));

            // (4) 실행 파일 폴더/dll/EwfTools
            AddCandidate(Path.Combine(baseDir, "dll", "EwfTools"));

            // (5) 일반적인 runtimes/win-x64(or win-x86)/native 폴더
            var arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";
            AddCandidate(Path.Combine(baseDir, "runtimes", arch, "native"));

            // 중복 제거 후 각 경로를 돌면서 실제 파일 존재 여부 확인
            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.GetFullPath(candidate);
                probedPaths.Add(fullPath);

                if (File.Exists(fullPath))
                {
                    return LoadFromFullPathInternal(fullPath);
                }
            }

            // 3) 위 경로들에서 못 찾은 경우: OS 기본 검색 경로에서 한 번 더 시도
            try
            {
                NativeLibrary.Load(libraryNameOrPath);
                // 로드는 되었지만, 실제 경로는 모르는 경우 이름만 반환
                return libraryNameOrPath;
            }
            catch (Exception ex)
            {
                // 검색한 모든 절대 경로를 예외 출력에 포함
                throw new NativeDllLoadException(libraryNameOrPath, probedPaths, ex);
            }
        }

        // 실제 절대 경로 기준으로 DLL을 로드하고 캐시에 보관
        private static string LoadFromFullPathInternal(string fullPath)
        {
            fullPath = Path.GetFullPath(fullPath);

            // 이미 로드된 경로라면 그대로 반환
            if (_loadedLibraries.ContainsKey(fullPath))
            {
                return fullPath;
            }

            // NativeLibrary.Load 로 실제 DLL 로드
            var handle = NativeLibrary.Load(fullPath);
            _loadedLibraries.TryAdd(fullPath, handle);

            return fullPath;
        }

        // DLL 파일의 버전 문자열을 반환. 실패 시 null.
        public static string? GetFileVersionFromPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return null;

            fullPath = Path.GetFullPath(fullPath);

            if (!File.Exists(fullPath))
                return null;

            try
            {
                var info = FileVersionInfo.GetVersionInfo(fullPath);

                if (!string.IsNullOrWhiteSpace(info.FileVersion))
                    return info.FileVersion;

                if (!string.IsNullOrWhiteSpace(info.ProductVersion))
                    return info.ProductVersion;

                return null;
            }
            catch
            {
                return null;
            }
        }

        // DLL 로드 실패 시 던지는 예외.
        public sealed class NativeDllLoadException : Exception
        {
            // 검색에 사용된 절대 경로 목록
            public IReadOnlyList<string> ProbedPaths { get; }

            public NativeDllLoadException(
                string libraryName,
                IEnumerable<string> probedPaths,
                Exception? inner = null)
                : base(BuildMessage(libraryName, probedPaths), inner)
            {
                ProbedPaths = probedPaths.ToArray();
            }

            // 예외 메시지 문자열 생성
            private static string BuildMessage(string libraryName, IEnumerable<string> probedPaths)
            {
                var baseDir = AppContext.BaseDirectory;
                var sb = new StringBuilder();

                sb.AppendLine($"DLL '{libraryName}' 을(를) 찾을 수 없습니다.");
                sb.AppendLine($"기본 폴더(실행 파일 기준): {Path.GetFullPath(baseDir)}");
                sb.AppendLine("다음 절대 경로들을 검색했습니다:");

                foreach (var p in probedPaths.Distinct())
                {
                    sb.AppendLine($"  - {p}");
                }

                return sb.ToString();
            }
        }
    }
}
