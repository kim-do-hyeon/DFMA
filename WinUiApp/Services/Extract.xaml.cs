using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

using DiscUtils.Ntfs;

namespace WinUiApp.Services
{
    // NTFS 파일/폴더를 실제 Storage로 추출하는 헬퍼
    internal static class ExtractHelper
    {
        // NTFS 단일 파일을 지정된 StorageFile로 추출
        public static async Task ExtractFileAsync(NtfsFileSystem ntfs, string ntfsPath, StorageFile destFile)
        {
            if (ntfs == null) throw new ArgumentNullException(nameof(ntfs));
            if (destFile == null) throw new ArgumentNullException(nameof(destFile));
            if (string.IsNullOrEmpty(ntfsPath)) throw new ArgumentNullException(nameof(ntfsPath));

            using (var src = ntfs.OpenFile(ntfsPath, FileMode.Open, FileAccess.Read))
            using (var dest = await destFile.OpenStreamForWriteAsync())
            {
                dest.SetLength(0);
                await src.CopyToAsync(dest);
            }
        }

        // NTFS 디렉터리 전체를 재귀적으로 StorageFolder로 추출
        public static async Task ExtractDirectoryAsync(NtfsFileSystem ntfs, string ntfsPath, StorageFolder destRoot)
        {
            if (ntfs == null) throw new ArgumentNullException(nameof(ntfs));
            if (destRoot == null) throw new ArgumentNullException(nameof(destRoot));
            if (string.IsNullOrEmpty(ntfsPath))
                ntfsPath = "\\";

            await ExtractDirectoryRecursiveAsync(ntfs, ntfsPath, destRoot, "");
        }

        // NTFS 디렉터리를 재귀적으로 순회하며 폴더와 파일을 생성/복사
        private static async Task ExtractDirectoryRecursiveAsync(
            NtfsFileSystem ntfs,
            string ntfsPath,
            StorageFolder destRoot,
            string relativePath)
        {
            StorageFolder currentFolder = destRoot;
            if (!string.IsNullOrEmpty(relativePath))
            {
                currentFolder = await destRoot.CreateFolderAsync(
                    relativePath,
                    CreationCollisionOption.OpenIfExists);
            }

            string path = ntfsPath;

            foreach (var dirPath in ntfs.GetDirectories(path))
            {
                string dirName = GetLastPathComponent(dirPath);
                if (string.IsNullOrEmpty(dirName))
                    dirName = "dir";

                string childRelative = string.IsNullOrEmpty(relativePath)
                    ? dirName
                    : relativePath + "\\" + dirName;

                await ExtractDirectoryRecursiveAsync(ntfs, dirPath, destRoot, childRelative);
            }

            foreach (var filePath in ntfs.GetFiles(path))
            {
                string fileName = GetLastPathComponent(filePath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = "unnamed.bin";

                StorageFile destFile = await currentFolder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.ReplaceExisting);

                await ExtractFileAsync(ntfs, filePath, destFile);
            }
        }

        // NTFS 경로에서 마지막 구성 요소(파일명/폴더명) 추출
        private static string GetLastPathComponent(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return fullPath;

            int idx = fullPath.LastIndexOf('\\');
            return idx >= 0 ? fullPath[(idx + 1)..] : fullPath;
        }
    }
}
