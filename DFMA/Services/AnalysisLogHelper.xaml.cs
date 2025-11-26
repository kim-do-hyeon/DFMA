using Microsoft.UI.Xaml.Controls;

using System;
using System.IO;
using System.Text;
using System.Text.Json;

using WinUiApp.Pages.ArtifactsAnalysis;

namespace WinUiApp.Services
{
    /// <summary>
    /// 케이스 루트에 analysis_log.log를 생성하고
    /// NDJSON(JSON Lines) 형태로 로그를 한 줄씩 Append 하는 유틸.
    /// </summary>
    // 케이스 분석 과정의 로그를 JSON 라인 형태로 파일에 기록하는 헬퍼 페이지/클래스
    public sealed partial class AnalysisLogHelper : Page
    {
        // 파일 쓰기 동시성 제어를 위한 락 객체
        private static readonly object _fileLock = new();

        // JSON 직렬화 옵션(압축 출력, 들여쓰기 없음)
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = false
        };

        // 단일 로그 한 줄의 구조를 표현하는 레코드 타입
        public record LogEntry(
            DateTimeOffset Timestamp,
            string Level,
            string Category,
            string Message,
            object? Data = null
        );

        /// <summary>
        /// 현재 케이스 루트 기준으로 로그 기록.
        /// </summary>
        // CaseImformation.CurrentCaseRoot를 사용해 현재 케이스 로그 파일에 한 줄 기록
        public static void WriteCurrentCase(
            string level,
            string category,
            string message,
            object? data = null)
        {
            var caseRoot = CaseImformation.CurrentCaseRoot;
            if (string.IsNullOrWhiteSpace(caseRoot))
                return; // 케이스 없으면 조용히 스킵

            Write(caseRoot, level, category, message, data);
        }

        /// <summary>
        /// 지정 케이스 루트에 로그 기록.
        /// </summary>
        // 명시된 케이스 루트 경로를 기준으로 analysis.log 파일에 JSON 로그를 추가
        public static void Write(
            string caseRoot,
            string level,
            string category,
            string message,
            object? data = null)
        {
            if (string.IsNullOrWhiteSpace(caseRoot)) return;

            try
            {
                Directory.CreateDirectory(caseRoot);

                var logPath = Path.Combine(caseRoot, "analysis.log");

                var entry = new LogEntry(
                    Timestamp: DateTimeOffset.UtcNow,
                    Level: level,
                    Category: category,
                    Message: message,
                    Data: data
                );

                var jsonLine = JsonSerializer.Serialize(entry, _jsonOptions);

                lock (_fileLock)
                {
                    File.AppendAllText(
                        logPath,
                        jsonLine + Environment.NewLine,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                    );
                }
            }
            catch
            {
                // 로깅 실패는 앱 기능을 깨지 않게 조용히 무시
            }
        }

        // INFO 레벨로 현재 케이스 로그에 기록하는 편의 메서드
        public static void Info(string category, string message, object? data = null)
            => WriteCurrentCase("INFO", category, message, data);

        // WARN 레벨로 현재 케이스 로그에 기록하는 편의 메서드
        public static void Warn(string category, string message, object? data = null)
            => WriteCurrentCase("WARN", category, message, data);

        // ERROR 레벨로 현재 케이스 로그에 기록하는 편의 메서드
        public static void Error(string category, string message, object? data = null)
            => WriteCurrentCase("ERROR", category, message, data);
    }
}
