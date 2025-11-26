using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using DiscUtils.Ntfs;

using WinUiApp;

#nullable enable

namespace WinUiApp.Services
{
    // NTFS 파일/폴더 메타데이터를 수집·가공하는 서비스
    public sealed class NtfsMetadataService
    {
        // NTFS 메타데이터의 정형 구조 모델
        public record MetadataInfo(
            string Name,
            string NtfsPath,
            ExplorerItem.ExplorerItemType Type,
            long SizeBytes,
            DateTime CreateUtc,
            DateTime ModifyUtc,
            DateTime AccessUtc,
            DateTime MftModifyUtc,
            ulong MftEntryNumber,
            ushort SequenceNumber,
            ulong RawFileRef
        );

        // ExplorerItem 기반으로 NTFS 메타데이터를 파싱
        public MetadataInfo Parse(NtfsFileSystem ntfs, ExplorerItem item)
        {
            var path = string.IsNullOrEmpty(item.FullPath) ? "\\" : item.FullPath;

            NtfsMetadataHelper.GetAllTimes(
                ntfs, path,
                out var createUtc, out var modifyUtc,
                out var accessUtc, out var mftModifyUtc
            );

            long size = 0;
            if (item.Type == ExplorerItem.ExplorerItemType.File)
            {
                size = NtfsMetadataHelper.GetFileSizeSafe(ntfs, path);
            }

            var (rawRef, mftEntry, seq) = GetFileRef(ntfs, path);

            return new MetadataInfo(
                item.Name,
                path,
                item.Type,
                size,
                createUtc, modifyUtc, accessUtc, mftModifyUtc,
                mftEntry, seq, rawRef
            );
        }

        // MetadataInfo를 사람이 읽기 쉬운 문자열로 출력
        public string FormatForDisplay(MetadataInfo info)
        {
            var sb = new StringBuilder()
                .AppendLine($"이름: {info.Name}")
                .AppendLine($"NTFS 경로: {info.NtfsPath}")
                .AppendLine($"타입: {info.Type}")
                .AppendLine($"크기(Size): {info.SizeBytes:N0} bytes")
                .AppendLine()
                .AppendLine($"Create      : {info.CreateUtc:yyyy-MM-dd HH:mm:ss.ffff} (UTC)")
                .AppendLine($"Modify(Data): {info.ModifyUtc:yyyy-MM-dd HH:mm:ss.ffff} (UTC)")
                .AppendLine($"Access      : {info.AccessUtc:yyyy-MM-dd HH:mm:ss.ffff} (UTC)")
                .AppendLine($"MFT Modify  : {info.MftModifyUtc:yyyy-MM-dd HH:mm:ss.ffff} (UTC)");

            if (info.MftEntryNumber != 0)
            {
                sb.AppendLine()
                  .AppendLine($"MFT Entry Number : {info.MftEntryNumber}")
                  .AppendLine($"Sequence Number  : {info.SequenceNumber}")
                  .AppendLine($"Raw File Ref     : 0x{info.RawFileRef:X16}");
            }

            return sb.ToString();
        }

        // 파일의 MFT 참조(Entry 번호 / Sequence)를 추출
        static (ulong rawRef, ulong mftEntry, ushort seq) GetFileRef(NtfsFileSystem ntfs, string path)
        {
            try
            {
                var raw = unchecked((ulong)ntfs.GetFileId(path));
                var entry = raw & 0x0000FFFFFFFFFFFFUL;
                var seq = (ushort)((raw >> 48) & 0xFFFF);
                return (raw, entry, seq);
            }
            catch { return default; }
        }
    }

    // NTFS 내부 시간/ADS/속성 등을 Reflection 기반으로 분석하는 헬퍼
    internal static class NtfsMetadataHelper
    {
        record TimeProps(PropertyInfo? C, PropertyInfo? M, PropertyInfo? A, PropertyInfo? MC);

        static readonly MethodInfo? GetFile;
        static readonly MethodInfo? GetStdInfo;
        static readonly MethodInfo? GetFnInfo;
        static TimeProps StdProps = new(null, null, null, null);
        static TimeProps FnProps = new(null, null, null, null);

        static readonly DateTime NtfsEpochUtc = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        sealed class VolumeTimeHolder { public DateTime BaseUtc; public bool HasValue; }
        static readonly ConditionalWeakTable<NtfsFileSystem, VolumeTimeHolder> _volBaseTimes = new();

        // NTFS 내부 구조에 대한 Reflection 메타정보 초기화
        static NtfsMetadataHelper()
        {
            try
            {
                var ntfsType = typeof(NtfsFileSystem);
                var asm = ntfsType.Assembly;

                GetFile = ntfsType.GetMethod("GetFile",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(string) }, null);

                var fileType = asm.GetType("DiscUtils.Ntfs.File");
                if (fileType == null) return;

                GetStdInfo = fileType.GetMethod("GetStandardInformation",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                var siType = asm.GetType("DiscUtils.Ntfs.FileStandardInformation")
                          ?? asm.GetType("DiscUtils.Ntfs.StandardInformation");

                if (siType != null)
                {
                    StdProps = new TimeProps(
                        GetProp(siType, "CreationTime"),
                        GetProp(siType, "ModificationTime", "LastWriteTime"),
                        GetProp(siType, "AccessTime", "LastAccessTime"),
                        GetProp(siType, "MftChangedTime", "MftChangeTime", "MftModificationTime")
                    );
                }

                GetFnInfo = fileType.GetMethod("GetFileNameInformation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                var fnType = GetFnInfo?.ReturnType;
                if (fnType != null)
                {
                    FnProps = new TimeProps(
                        GetProp(fnType, "CreationTime"),
                        GetProp(fnType, "ModificationTime", "LastWriteTime"),
                        GetProp(fnType, "AccessTime", "LastAccessTime"),
                        GetProp(fnType, "MftChangedTime", "MftChangeTime", "MftModificationTime")
                    );
                }
            }
            catch { }
        }

        // 여러 이름 후보 중 존재하는 PropertyInfo 반환
        static PropertyInfo? GetProp(Type t, params string[] names)
            => names.Select(t.GetProperty).FirstOrDefault(p => p != null);

        // object → DateTime 변환 유틸리티
        static DateTime FromObj(object? v) => v is DateTime dt ? dt : DateTime.MinValue;

        // NTFS Epoch 기준으로 "의심스러운" 시간 판정
        static bool IsSuspicious(DateTime dt)
        {
            if (dt == DateTime.MinValue) return true;
            var utc = dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime();
            return utc <= NtfsEpochUtc.AddDays(1);
        }

        // 전달된 메타객체에서 시간 속성들을 추출
        static void ReadTimes(object? info, TimeProps p,
            ref DateTime c, ref DateTime m, ref DateTime a, ref DateTime mc)
        {
            if (info == null) return;
            if (p.C != null) c = FromObj(p.C.GetValue(info));
            if (p.M != null) m = FromObj(p.M.GetValue(info));
            if (p.A != null) a = FromObj(p.A.GetValue(info));
            if (p.MC != null) mc = FromObj(p.MC.GetValue(info));
        }

        // ADS 경로 분리(파일 본체 / 스트림명)
        static (string plain, string? stream) SplitAdsPath(string path)
        {
            var idx = path.IndexOf(':');
            if (idx < 0) return (path, null);
            return (path.Substring(0, idx), path[(idx + 1)..]);
        }

        // NTFS 파일의 Named Data Stream 목록을 추출
        public static List<string> ListNamedDataStreams(NtfsFileSystem ntfs, string path)
        {
            var (plainPath, _) = SplitAdsPath(path);
            var result = new List<string>();

            void AddName(string? name)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                result.Add(name.Trim());
            }

            static string NormalizeStreamName(string raw, string plainPath)
            {
                if (string.IsNullOrWhiteSpace(raw)) return raw;
                var s = raw.Trim();

                var colon = s.IndexOf(':');
                if (colon >= 0)
                {
                    var left = s.Substring(0, colon);
                    if (left.Equals(plainPath, StringComparison.OrdinalIgnoreCase) ||
                        left.EndsWith(Path.GetFileName(plainPath), StringComparison.OrdinalIgnoreCase) ||
                        left.Length == 0)
                    {
                        s = s[(colon + 1)..];
                    }
                }

                if (s.Equals("$DATA", StringComparison.OrdinalIgnoreCase)) return "";
                return s.TrimStart(':');
            }

            bool IsDataAttribute(object? typeObj)
            {
                var typeStr = typeObj?.ToString() ?? "";
                return typeStr.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                       typeStr.EndsWith("Data", StringComparison.OrdinalIgnoreCase);
            }

            void ExtractFromAttributes(IEnumerable attrs)
            {
                foreach (var attr in attrs)
                {
                    if (attr == null) continue;
                    var at = attr.GetType();

                    var nameProp = at.GetProperty("Name")
                                ?? at.GetProperty("AttributeName")
                                ?? at.GetProperty("StreamName");
                    var typeProp = at.GetProperty("Type")
                                ?? at.GetProperty("AttributeType");

                    var nameObj = nameProp?.GetValue(attr);
                    string? name = null;

                    if (nameObj is byte[] bytes)
                        name = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                    else
                        name = nameObj?.ToString();

                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!IsDataAttribute(typeProp?.GetValue(attr))) continue;

                    AddName(name);
                }
            }

            void ExtractFromMftRecords(object fileObj)
            {
                var ft = fileObj.GetType();

                var mftRecProp = ft.GetProperty("MftRecord",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mftRec = mftRecProp?.GetValue(fileObj);
                if (mftRec != null)
                {
                    var attrsProp = mftRec.GetType().GetProperty("Attributes",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (attrsProp?.GetValue(mftRec) is IEnumerable recAttrs)
                        ExtractFromAttributes(recAttrs);
                }

                var recsProp = ft.GetProperty("Records",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (recsProp?.GetValue(fileObj) is IEnumerable recs)
                {
                    foreach (var r in recs)
                    {
                        if (r == null) continue;
                        var attrsProp = r.GetType().GetProperty("Attributes",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (attrsProp?.GetValue(r) is IEnumerable recAttrs)
                            ExtractFromAttributes(recAttrs);
                    }
                }
            }

            try
            {
                var mi = ntfs.GetType().GetMethod("GetAlternateDataStreams",
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                             null, new[] { typeof(string) }, null)
                      ?? ntfs.GetType().GetMethod("GetDataStreams",
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                             null, new[] { typeof(string) }, null);

                if (mi != null && mi.Invoke(ntfs, new object[] { plainPath }) is IEnumerable streams)
                {
                    foreach (var s in streams)
                    {
                        var raw = s?.ToString();
                        var norm = NormalizeStreamName(raw ?? "", plainPath);
                        AddName(norm);
                    }
                }
            }
            catch { }

            if (result.Count == 0 && GetFile != null)
            {
                try
                {
                    var fileObj = GetFile.Invoke(ntfs, new object[] { plainPath });
                    if (fileObj != null)
                    {
                        IEnumerable? attrs = null;
                        var ft = fileObj.GetType();

                        var allAttrProp =
                            ft.GetProperty("AllAttributes",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? ft.GetProperty("Attributes",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                        if (allAttrProp?.GetValue(fileObj) is IEnumerable a)
                            attrs = a;

                        if (attrs != null)
                            ExtractFromAttributes(attrs);

                        ExtractFromMftRecords(fileObj);
                    }
                }
                catch { }
            }

            if (result.Count == 0)
            {
                try
                {
                    var trimmed = plainPath.TrimEnd('\\');
                    var idx = trimmed.LastIndexOf('\\');
                    var lastName = (idx >= 0) ? trimmed[(idx + 1)..] : trimmed;

                    if (lastName.Equals("$UsnJrnl", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var cand in new[] { "$J", "$Max" })
                        {
                            try
                            {
                                using var s = ntfs.OpenFile($"{plainPath}:{cand}", FileMode.Open, FileAccess.Read);
                                if (s != null) AddName(cand);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            return result
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // 스트림 API → 파일 길이 가져오기 (ADS 포함)
        public static long GetFileSizeSafe(NtfsFileSystem ntfs, string path)
        {
            try
            {
                var mi = ntfs.GetType().GetMethod("GetFileLength", new[] { typeof(string) });
                if (mi != null && mi.Invoke(ntfs, new object[] { path }) is long len)
                    return len;
            }
            catch { }

            try
            {
                using var s = ntfs.OpenFile(path, FileMode.Open, FileAccess.Read);
                return s.Length;
            }
            catch
            {
                return 0;
            }
        }

        // NTFS 시간(생성/수정/액세스/MFT 수정)을 종합적으로 취득
        public static void GetAllTimes(
            NtfsFileSystem ntfs, string path,
            out DateTime createUtc, out DateTime modifyUtc,
            out DateTime accessUtc, out DateTime mftModifyUtc)
        {
            createUtc = modifyUtc = accessUtc = mftModifyUtc = DateTime.MinValue;
            object? fileObj = null;

            var (plainPath, _) = SplitAdsPath(path);

            try
            {
                if (GetFile != null && GetStdInfo != null)
                {
                    fileObj = GetFile.Invoke(ntfs, new object[] { plainPath });
                    var stdInfo = fileObj != null ? GetStdInfo.Invoke(fileObj, null) : null;
                    ReadTimes(stdInfo, StdProps, ref createUtc, ref modifyUtc, ref accessUtc, ref mftModifyUtc);
                }
            }
            catch { }

            if ((IsSuspicious(createUtc) || IsSuspicious(modifyUtc) || IsSuspicious(accessUtc) || IsSuspicious(mftModifyUtc)) && GetFnInfo != null)
            {
                try
                {
                    fileObj ??= GetFile?.Invoke(ntfs, new object[] { plainPath });
                    var fnInfo = fileObj != null ? GetFnInfo.Invoke(fileObj, null) : null;

                    var c = createUtc; var m = modifyUtc; var a = accessUtc; var mc = mftModifyUtc;
                    ReadTimes(fnInfo, FnProps, ref c, ref m, ref a, ref mc);

                    if (!IsSuspicious(c)) createUtc = c;
                    if (!IsSuspicious(m)) modifyUtc = m;
                    if (!IsSuspicious(a)) accessUtc = a;
                    if (!IsSuspicious(mc)) mftModifyUtc = mc;
                }
                catch { }
            }

            TryFallback(() => ntfs.GetCreationTimeUtc(path), ref createUtc);
            TryFallback(() => ntfs.GetLastWriteTimeUtc(path), ref modifyUtc);
            TryFallback(() => ntfs.GetLastAccessTimeUtc(path), ref accessUtc);

            if (IsSuspicious(mftModifyUtc))
            {
                if (!IsSuspicious(modifyUtc)) mftModifyUtc = modifyUtc;
                else if (!IsSuspicious(createUtc)) mftModifyUtc = createUtc;
            }

            if (IsRootMetafile(plainPath) &&
                IsSuspicious(createUtc) && IsSuspicious(modifyUtc) && IsSuspicious(accessUtc))
            {
                var baseUtc = GetVolumeBaseUtc(ntfs);

                createUtc = baseUtc;
                modifyUtc = baseUtc;
                accessUtc = baseUtc;
                if (IsSuspicious(mftModifyUtc))
                    mftModifyUtc = baseUtc;
            }

            void TryFallback(Func<DateTime> getter, ref DateTime target)
            {
                try
                {
                    if (IsSuspicious(target))
                        target = getter();
                }
                catch { }
            }
        }

        // NTFS 시스템 파일(메타파일) 여부 확인
        static bool IsRootMetafile(string plainPath)
        {
            if (string.IsNullOrEmpty(plainPath)) return false;
            if (!plainPath.StartsWith("\\")) plainPath = "\\" + plainPath;

            var remainder = plainPath.Trim('\\');
            if (remainder.Contains('\\')) return false;
            return remainder.StartsWith('$');
        }

        // 볼륨 생성 시점을 추정해 기본 시간으로 사용
        static DateTime GetVolumeBaseUtc(NtfsFileSystem ntfs)
        {
            var holder = _volBaseTimes.GetOrCreateValue(ntfs);
            if (holder.HasValue) return holder.BaseUtc;

            DateTime t = DateTime.MinValue;

            try { t = ntfs.GetCreationTimeUtc(@"\$MFT"); } catch { }
            if (IsSuspicious(t))
            {
                try { t = ntfs.GetLastWriteTimeUtc(@"\$MFT"); } catch { }
            }
            if (IsSuspicious(t))
            {
                try { t = ntfs.GetCreationTimeUtc(@"\"); } catch { }
            }
            if (IsSuspicious(t))
                t = DateTime.UtcNow;

            holder.BaseUtc = t;
            holder.HasValue = true;
            return t;
        }
    }
}
