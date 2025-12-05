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
    // NTFS ����/���� ��Ÿ�����͸� �����������ϴ� ����
    public sealed class NtfsMetadataService
    {
        // NTFS ��Ÿ�������� ���� ���� ��
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

        // ExplorerItem ������� NTFS ��Ÿ�����͸� �Ľ�
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

        // MetadataInfo�� ����� �б� ���� ���ڿ��� ���
        public string FormatForDisplay(MetadataInfo info)
        {
            var sb = new StringBuilder()
                .AppendLine($"�̸�: {info.Name}")
                .AppendLine($"NTFS ���: {info.NtfsPath}")
                .AppendLine($"Ÿ��: {info.Type}")
                .AppendLine($"ũ��(Size): {info.SizeBytes:N0} bytes")
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

        // ������ MFT ����(Entry ��ȣ / Sequence)�� ����
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

    // NTFS ���� �ð�/ADS/�Ӽ� ���� Reflection ������� �м��ϴ� ����
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

        // NTFS ���� ������ ���� Reflection ��Ÿ���� �ʱ�ȭ
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

        // ���� �̸� �ĺ� �� �����ϴ� PropertyInfo ��ȯ
        static PropertyInfo? GetProp(Type t, params string[] names)
            => names.Select(t.GetProperty).FirstOrDefault(p => p != null);

        // object �� DateTime ��ȯ ��ƿ��Ƽ
        static DateTime FromObj(object? v) => v is DateTime dt ? dt : DateTime.MinValue;

        // NTFS Epoch �������� "�ǽɽ�����" �ð� ����
        static bool IsSuspicious(DateTime dt)
        {
            if (dt == DateTime.MinValue) return true;
            var utc = dt.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
                : dt.ToUniversalTime();
            return utc <= NtfsEpochUtc.AddDays(1);
        }

        // ���޵� ��Ÿ��ü���� �ð� �Ӽ����� ����
        static void ReadTimes(object? info, TimeProps p,
            ref DateTime c, ref DateTime m, ref DateTime a, ref DateTime mc)
        {
            if (info == null) return;
            if (p.C != null) c = FromObj(p.C.GetValue(info));
            if (p.M != null) m = FromObj(p.M.GetValue(info));
            if (p.A != null) a = FromObj(p.A.GetValue(info));
            if (p.MC != null) mc = FromObj(p.MC.GetValue(info));
        }

        // ADS ��� �и�(���� ��ü / ��Ʈ����)
        static (string plain, string? stream) SplitAdsPath(string path)
        {
            var idx = path.IndexOf(':');
            if (idx < 0) return (path, null);
            return (path.Substring(0, idx), path[(idx + 1)..]);
        }

        // NTFS ������ Named Data Stream ����� ����
        public static List<string> ListNamedDataStreams(NtfsFileSystem ntfs, string path)
        {
            var (plainPath, _) = SplitAdsPath(path);
            var result = new List<string>();
            var includeNonData = IsRootMetafile(plainPath);

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

                    var isData = IsDataAttribute(typeProp?.GetValue(attr));
                    if (isData || includeNonData)
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

        // ��Ʈ�� API �� ���� ���� �������� (ADS ����)
        public static long GetFileSizeSafe(NtfsFileSystem ntfs, string path)
        {
            var (plainPath, adsName) = SplitAdsPath(path);
            long result = 0;
            string debugInfo = "";
            long bestDataLength = 0;

            void TrackCandidateValue(long candidate)
            {
                if (candidate > bestDataLength)
                    bestDataLength = candidate;
            }

            void TrackCandidate(object? value)
            {
                if (value is long l && l >= 0) TrackCandidateValue(l);
                else if (value is ulong u) TrackCandidateValue(unchecked((long)u));
                else if (value is int i && i >= 0) TrackCandidateValue(i);
            }

            // DATA 속성인지 확인하는 로컬 함수
            bool IsDataAttribute(object? typeObj)
            {
                var typeStr = typeObj?.ToString() ?? "";
                return typeStr.Equals("Data", StringComparison.OrdinalIgnoreCase) ||
                       typeStr.EndsWith("Data", StringComparison.OrdinalIgnoreCase);
            }

            // 1. GetFileLength 리플렉션 시도 (0보다 큰 값만 유효)
            try
            {
                var mi = ntfs.GetType().GetMethod("GetFileLength", new[] { typeof(string) });
                if (mi != null && mi.Invoke(ntfs, new object[] { path }) is long len && len > 0)
                {
                    result = len;
                    debugInfo = $"GetFileLength: {len}";
                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                    return len;
                }
                else if (mi != null)
                {
                    var lenValue = mi.Invoke(ntfs, new object[] { path });
                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: GetFileLength returned {lenValue}, continuing...");
                }
            }
            catch (Exception ex)
            {
                debugInfo = $"GetFileLength failed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: {debugInfo}");
            }

            // 2. StandardInformation에서 파일 크기 읽기 (특수 파일도 처리 가능)
            try
            {
                if (GetFile != null && GetStdInfo != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Attempting to get File object...");
                    var fileObj = GetFile.Invoke(ntfs, new object[] { plainPath });
                    if (fileObj != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: File object obtained, getting StandardInformation...");
                        var stdInfo = GetStdInfo.Invoke(fileObj, null);
                        if (stdInfo != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: StandardInformation obtained");
                            // StandardInformation의 모든 속성 확인
                            var stdInfoType = stdInfo.GetType();
                            var allProps = stdInfoType.GetProperties(
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: StandardInfo has {allProps.Length} properties");

                            // 가능한 파일 크기 속성 이름들
                            var sizePropNames = new[] { "AllocatedSize", "LogicalSize", "FileSize", 
                                "Size", "Length", "EndOfFile", "ValidDataLength" };

                            foreach (var propName in sizePropNames)
                            {
                                var prop = allProps.FirstOrDefault(p => 
                                    p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
                                if (prop != null)
                                {
                                    var value = prop.GetValue(stdInfo);
                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: StandardInfo.{propName} = {value} (type: {value?.GetType().Name})");
                                    if (value is long size && size > 0)
                                    {
                                        result = size;
                                        debugInfo = $"StandardInfo.{propName}: {size}";
                                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                        return size;
                                    }
                                    if (value is ulong uSize && uSize > 0 && uSize <= long.MaxValue)
                                    {
                                        result = (long)uSize;
                                        debugInfo = $"StandardInfo.{propName}: {(long)uSize}";
                                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                        return (long)uSize;
                                    }
                                    if (value is int iSize && iSize > 0)
                                    {
                                        result = iSize;
                                        debugInfo = $"StandardInfo.{propName}: {iSize}";
                                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                        return iSize;
                                    }
                                }
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: StandardInformation is null");
                        }

                        // File 객체의 Length 속성 시도
                        var lengthProp = fileObj.GetType().GetProperty("Length",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (lengthProp != null)
                        {
                            var fileLenValue = lengthProp.GetValue(fileObj);
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: File.Length = {fileLenValue} (type: {fileLenValue?.GetType().Name})");
                            if (fileLenValue is long fileLen && fileLen > 0)
                            {
                                result = fileLen;
                                debugInfo = $"File.Length: {fileLen}";
                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                return fileLen;
                            }
                        }

                        // MFT Record에서 Data 속성의 실제 크기 읽기
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Attempting to read MFT Record...");
                        var mftRecProp = fileObj.GetType().GetProperty("MftRecord",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        var mftRec = mftRecProp?.GetValue(fileObj);
                        if (mftRec != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: MFT Record obtained");
                            var attrsProp = mftRec.GetType().GetProperty("Attributes",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (attrsProp?.GetValue(mftRec) is IEnumerable attrs)
                            {
                                int attrCount = 0;
                                foreach (var attr in attrs)
                                {
                                    attrCount++;
                                    if (attr == null) continue;

                                    // 속성 타입 확인
                                    var typeProp = attr.GetType().GetProperty("Type")
                                                ?? attr.GetType().GetProperty("AttributeType");
                                    var attrType = typeProp?.GetValue(attr);
                                    
                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Attribute #{attrCount}, Type = {attrType}");
                                    
                                    // DATA 속성인지 확인
                                    if (!IsDataAttribute(attrType)) continue;
                                    
                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Found DATA attribute");

                                    // 속성 이름 확인 (ADS인 경우)
                                    var nameProp = attr.GetType().GetProperty("Name")
                                                ?? attr.GetType().GetProperty("AttributeName")
                                                ?? attr.GetType().GetProperty("StreamName");
                                    var nameObj = nameProp?.GetValue(attr);
                                    string? attrName = null;
                                    if (nameObj is byte[] bytes)
                                        attrName = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                                    else
                                        attrName = nameObj?.ToString();
                                    
                                    if (string.IsNullOrEmpty(adsName))
                                    {
                                        // 기본 데이터 스트림인 경우 (이름이 없거나 빈 문자열)
                                        if (string.IsNullOrEmpty(attrName))
                                        {
                                            // AllocatedLength 시도
                                            var allocatedProp = attr.GetType().GetProperty("AllocatedLength",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (allocatedProp != null)
                                            {
                                                var allocatedValue = allocatedProp.GetValue(attr);
                                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: DataAttr.AllocatedLength = {allocatedValue}");
                                                if (allocatedValue is long allocatedLen && allocatedLen > 0)
                                                {
                                                    result = allocatedLen;
                                                    debugInfo = $"DataAttr.AllocatedLength: {allocatedLen}";
                                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                                    return allocatedLen;
                                                }
                                            }

                                            // ActualSize 시도
                                            var actualSizeProp = attr.GetType().GetProperty("ActualSize",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (actualSizeProp != null)
                                            {
                                                var actualValue = actualSizeProp.GetValue(attr);
                                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: DataAttr.ActualSize = {actualValue}");
                                                if (actualValue is long actualLen && actualLen > 0)
                                                {
                                                    result = actualLen;
                                                    debugInfo = $"DataAttr.ActualSize: {actualLen}";
                                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                                    return actualLen;
                                                }
                                            }

                                            // Length 속성 시도
                                            var lengthAttrProp = attr.GetType().GetProperty("Length",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (lengthAttrProp != null)
                                            {
                                                var lengthValue = lengthAttrProp.GetValue(attr);
                                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: DataAttr.Length = {lengthValue}");
                                                if (lengthValue is long attrLen && attrLen > 0)
                                                {
                                                    result = attrLen;
                                                    debugInfo = $"DataAttr.Length: {attrLen}";
                                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                                    return attrLen;
                                                }
                                            }
                                            
                                            // 모든 속성 나열 (디버깅용)
                                            var allAttrProps = attr.GetType().GetProperties(
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: DataAttr has {allAttrProps.Length} properties");
                                            foreach (var ap in allAttrProps.Take(10))
                                            {
                                                try
                                                {
                                                    var apValue = ap.GetValue(attr);
                                                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: DataAttr.{ap.Name} = {apValue}");
                                                }
                                                catch { }
                                            }
                                        }
                                        else
                                        {
                                            // 기본 데이터 스트림이 없어도 이름 있는 DATA 속성 크기를 후보로 저장
                                            var allocatedProp = attr.GetType().GetProperty("AllocatedLength",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            TrackCandidate(allocatedProp?.GetValue(attr));

                                            var actualSizeProp = attr.GetType().GetProperty("ActualSize",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            TrackCandidate(actualSizeProp?.GetValue(attr));

                                            var lengthAttrProp = attr.GetType().GetProperty("Length",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            TrackCandidate(lengthAttrProp?.GetValue(attr));
                                        }
                                    }
                                    else
                                    {
                                        // ADS인 경우 이름 매칭
                                        if (attrName?.Equals(adsName, StringComparison.OrdinalIgnoreCase) == true)
                                        {
                                            var allocatedProp = attr.GetType().GetProperty("AllocatedLength",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (allocatedProp?.GetValue(attr) is long allocatedLen && allocatedLen >= 0)
                                                return allocatedLen;

                                            var actualSizeProp = attr.GetType().GetProperty("ActualSize",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (actualSizeProp?.GetValue(attr) is long actualLen && actualLen >= 0)
                                                return actualLen;

                                            var lengthAttrProp = attr.GetType().GetProperty("Length",
                                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                                            if (lengthAttrProp?.GetValue(attr) is long attrLen && attrLen >= 0)
                                                return attrLen;
                                        }
                                    }
                                }
                            }

                            if (result == 0 && bestDataLength > 0)
                            {
                                result = bestDataLength;
                                debugInfo = $"Fallback DATA attribute length: {bestDataLength}";
                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                return result;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. 파일 스트림 열어서 Length 읽기 (마지막 수단)
            try
            {
                using var s = ntfs.OpenFile(path, FileMode.Open, FileAccess.Read);
                if (s != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Stream opened successfully");
                    System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Stream.Length = {s.Length}, CanSeek = {s.CanSeek}");
                    
                    // Length 속성 확인 (0보다 큰 값만 유효)
                    if (s.Length > 0)
                    {
                        result = s.Length;
                        debugInfo = $"Stream.Length: {s.Length}";
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                        return s.Length;
                    }
                    else if (s.Length == 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Stream.Length is 0, trying Seek method...");
                    }

                    // Seek 가능한 경우 파일 끝으로 이동해서 Position 확인
                    if (s.CanSeek)
                    {
                        try
                        {
                            var originalPos = s.Position;
                            s.Seek(0, SeekOrigin.End);
                            var fileSize = s.Position;
                            s.Position = originalPos;
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Seek to end, Position = {fileSize}");
                            if (fileSize > 0)
                            {
                                result = fileSize;
                                debugInfo = $"Stream.Seek(End): {fileSize}";
                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                                return fileSize;
                            }
                            else if (fileSize == 0)
                            {
                                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Stream.Seek(End) returned 0");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Seek failed: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: Stream open failed: {ex.Message}");
            }

            // 4. Named data streams을 직접 열어보는 최종 시도 (예: $Secure:$SDS 등)
            if (result == 0)
            {
                bool hasNamedStreams = false;
                try
                {
                    foreach (var stream in ListNamedDataStreams(ntfs, plainPath))
                    {
                        hasNamedStreams = true;
                        try
                        {
                            using var s = ntfs.OpenFile($"{plainPath}:{stream}", FileMode.Open, FileAccess.Read);
                            if (s == null) continue;

                            var len = s.Length;
                            if (len == 0 && s.CanSeek)
                            {
                                var orig = s.Position;
                                s.Seek(0, SeekOrigin.End);
                                len = s.Position;
                                s.Position = orig;
                            }

                            if (len > result)
                            {
                                result = len;
                                debugInfo = $"Named stream '{stream}' length: {len}";
                            }
                        }
                        catch { }
                    }

                    if (result > 0)
                    {
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                        return result;
                    }

                    // NTFS 메타파일처럼 기본 데이터 스트림이 없지만 이름 있는 스트림만 있는 경우,
                    // FTK 등에서 1바이트로 표시하는 것과 맞추기 위해 최소 1바이트로 리턴.
                    if (hasNamedStreams && IsRootMetafile(plainPath))
                    {
                        result = 1;
                        debugInfo = "Root metafile with only named streams; returning 1 byte";
                        System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: SUCCESS - {debugInfo}");
                        return result;
                    }
                }
                catch { }
            }

            System.Diagnostics.Debug.WriteLine($"[GetFileSizeSafe] {path}: FAILED - All methods returned 0 or failed");
            return 0;
        }

        // NTFS �ð�(����/����/�׼���/MFT ����)�� ���������� ���
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

        // NTFS �ý��� ����(��Ÿ����) ���� Ȯ��
        static bool IsRootMetafile(string plainPath)
        {
            if (string.IsNullOrEmpty(plainPath)) return false;
            if (!plainPath.StartsWith("\\")) plainPath = "\\" + plainPath;

            var remainder = plainPath.Trim('\\');
            if (remainder.Contains('\\')) return false;
            return remainder.StartsWith('$');
        }

        // ���� ���� ������ ������ �⺻ �ð����� ���
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
