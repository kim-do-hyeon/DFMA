using Microsoft.Data.Sqlite;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinUiApp.Interop
{
    // Microsoft.Data.Sqlite를 사용하여 SQLite 작업 수행
    // 기존 IntPtr 기반 API와의 호환성을 위해 래퍼 제공
    internal static class NativeSqliteHelper
    {
        public const int SQLITE_OK = 0;
        public const int SQLITE_OPEN_READWRITE = 0x00000002;
        public const int SQLITE_OPEN_CREATE = 0x00000004;

        // IntPtr을 SqliteConnection으로 매핑하는 딕셔너리
        private static readonly ConcurrentDictionary<IntPtr, SqliteConnection> _connections = new();

        // IntPtr 핸들 생성용 카운터
        private static long _handleCounter = 1;

        public static int sqlite3_open_v2(
            string filename,
            out IntPtr db,
            int flags,
            string? vfs)
        {
            try
            {
                var connectionStringBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = filename
                };

                var connection = new SqliteConnection(connectionStringBuilder.ConnectionString);
                connection.Open();

                // IntPtr 핸들 생성 (고유한 값 사용)
                var handle = new IntPtr(Interlocked.Increment(ref _handleCounter));
                _connections.TryAdd(handle, connection);

                db = handle;
                return SQLITE_OK;
            }
            catch
            {
                db = IntPtr.Zero;
                return 1; // SQLITE_ERROR
            }
        }

        public static int sqlite3_close(IntPtr db)
        {
            if (db == IntPtr.Zero)
                return SQLITE_OK;

            if (_connections.TryRemove(db, out var connection))
            {
                try
                {
                    connection.Dispose();
                }
                catch
                {
                    return 1; // SQLITE_ERROR
                }
            }

            return SQLITE_OK;
        }

        public static int sqlite3_exec(
            IntPtr db,
            string sql,
            IntPtr callback,
            IntPtr arg,
            out IntPtr errMsg)
        {
            errMsg = IntPtr.Zero;

            if (db == IntPtr.Zero || !_connections.TryGetValue(db, out var connection))
            {
                return 1; // SQLITE_ERROR
            }

            try
            {
                var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
                return SQLITE_OK;
            }
            catch (SqliteException ex)
            {
                // 에러 메시지는 반환하지 않음 (기존 코드와 호환)
                return ex.SqliteErrorCode;
            }
            catch
            {
                return 1; // SQLITE_ERROR
            }
        }

        public static void sqlite3_free(IntPtr ptr)
        {
            // Microsoft.Data.Sqlite는 메모리 관리를 자동으로 처리하므로
            // 이 함수는 아무 작업도 하지 않음
        }

        // SQL 쿼리를 실행하는 헬퍼 메서드
        public static void ExecNonQuery(IntPtr db, string sql)
        {
            if (db == IntPtr.Zero || !_connections.TryGetValue(db, out var connection))
            {
                throw new InvalidOperationException("SQLite 연결이 유효하지 않습니다.");
            }

            try
            {
                var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException($"SQLite 오류: {ex.Message}", ex);
            }
        }

        // case_info 테이블에 key/value 쌍을 삽입하는 헬퍼 메서드
        public static void InsertKeyValue(IntPtr db, string key, string value)
        {
            if (db == IntPtr.Zero || !_connections.TryGetValue(db, out var connection))
            {
                throw new InvalidOperationException("SQLite 연결이 유효하지 않습니다.");
            }

            try
            {
                var command = connection.CreateCommand();
                command.CommandText = "INSERT OR REPLACE INTO case_info(key, value) VALUES(@key, @value)";
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                command.ExecuteNonQuery();
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException($"SQLite 오류: {ex.Message}", ex);
            }
        }

        // 콜백을 지원하는 sqlite3_exec 메서드
        // ExecCallback 델리게이트 타입
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate int ExecCallback(
            IntPtr arg,
            int columnCount,
            IntPtr columnValues,
            IntPtr columnNames);

        // UTF-8 바이트 배열을 문자열로 변환하는 헬퍼 메서드
        // NativeSqliteHelper는 UTF-8로 인코딩하므로 ANSI가 아닌 UTF-8로 디코딩해야 함
        public static string? PtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return null;

            // null-terminated UTF-8 문자열 읽기
            var bytes = new List<byte>();
            int offset = 0;
            while (true)
            {
                byte b = Marshal.ReadByte(ptr, offset);
                if (b == 0)
                    break;
                bytes.Add(b);
                offset++;
            }

            if (bytes.Count == 0)
                return null;

            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        // 콜백을 사용하는 sqlite3_exec 오버로드
        // 내부적으로 Microsoft.Data.Sqlite를 사용하여 구현
        public static int sqlite3_exec(
            IntPtr db,
            string sql,
            ExecCallback? callback,
            IntPtr arg,
            out IntPtr errMsg)
        {
            errMsg = IntPtr.Zero;

            if (db == IntPtr.Zero || !_connections.TryGetValue(db, out var connection))
            {
                return 1; // SQLITE_ERROR
            }

            try
            {
                if (callback != null)
                {
                    // SELECT 쿼리인 경우 DataReader를 사용하여 결과를 콜백에 전달
                    var command = connection.CreateCommand();
                    command.CommandText = sql;
                    
                    using (var reader = command.ExecuteReader())
                    {
                        if (reader.HasRows)
                        {
                            int columnCount = reader.FieldCount;
                            
                            // 각 행을 읽어서 콜백 호출
                            while (reader.Read())
                            {
                                // 컬럼 이름과 값을 임시로 저장
                                var columnNames = new List<IntPtr>();
                                var columnValues = new List<IntPtr>();
                                var nameHandles = new List<GCHandle>();
                                var valueHandles = new List<GCHandle>();

                                try
                                {
                                    for (int i = 0; i < columnCount; i++)
                                    {
                                        string colName = reader.GetName(i);
                                        string? colValue = reader.IsDBNull(i) ? null : reader.GetString(i);

                                        // ANSI 문자열로 변환
                                        byte[] nameBytes = Encoding.UTF8.GetBytes(colName + "\0");
                                        byte[] valueBytes = colValue != null 
                                            ? Encoding.UTF8.GetBytes(colValue + "\0")
                                            : new byte[] { 0 };

                                        // 고정된 메모리 주소를 가져오기 위해 GCHandle 사용
                                        var nameHandle = GCHandle.Alloc(nameBytes, GCHandleType.Pinned);
                                        var valueHandle = GCHandle.Alloc(valueBytes, GCHandleType.Pinned);
                                        
                                        nameHandles.Add(nameHandle);
                                        valueHandles.Add(valueHandle);
                                        
                                        columnNames.Add(nameHandle.AddrOfPinnedObject());
                                        columnValues.Add(valueHandle.AddrOfPinnedObject());
                                    }

                                    // 포인터 배열을 IntPtr로 변환
                                    IntPtr columnNamesPtr = Marshal.AllocHGlobal(columnCount * IntPtr.Size);
                                    IntPtr columnValuesPtr = Marshal.AllocHGlobal(columnCount * IntPtr.Size);

                                    try
                                    {
                                        for (int i = 0; i < columnCount; i++)
                                        {
                                            Marshal.WriteIntPtr(columnNamesPtr + i * IntPtr.Size, columnNames[i]);
                                            Marshal.WriteIntPtr(columnValuesPtr + i * IntPtr.Size, columnValues[i]);
                                        }

                                        int callbackResult = callback(arg, columnCount, columnValuesPtr, columnNamesPtr);
                                        if (callbackResult != 0)
                                        {
                                            return callbackResult;
                                        }
                                    }
                                    finally
                                    {
                                        Marshal.FreeHGlobal(columnNamesPtr);
                                        Marshal.FreeHGlobal(columnValuesPtr);
                                    }
                                }
                                finally
                                {
                                    // 모든 GCHandle 해제
                                    foreach (var handle in nameHandles)
                                        handle.Free();
                                    foreach (var handle in valueHandles)
                                        handle.Free();
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 콜백이 없는 경우 일반 쿼리 실행
                    var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.ExecuteNonQuery();
                }

                return SQLITE_OK;
            }
            catch (SqliteException ex)
            {
                return ex.SqliteErrorCode;
            }
            catch
            {
                return 1; // SQLITE_ERROR
            }
        }
    }
}
