using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace WinUiApp.Interop
{
    // dll\sqlite3.dll 에 대한 P/Invoke 래퍼.
    internal static class NativeSqliteHelper
    {
        public const int SQLITE_OK = 0;
        public const int SQLITE_OPEN_READWRITE = 0x00000002;
        public const int SQLITE_OPEN_CREATE = 0x00000004;

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int sqlite3_open_v2(
            string filename,
            out IntPtr db,
            int flags,
            string? vfs);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern int sqlite3_close(IntPtr db);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int sqlite3_exec(
            IntPtr db,
            string sql,
            IntPtr callback,
            IntPtr arg,
            out IntPtr errMsg);

        [DllImport("sqlite3", CallingConvention = CallingConvention.Cdecl)]
        public static extern void sqlite3_free(IntPtr ptr);

        // 에러 시 예외를 던집니다.
        public static void ExecNonQuery(IntPtr db, string sql)
        {
            IntPtr errPtr;
            int rc = sqlite3_exec(db, sql, IntPtr.Zero, IntPtr.Zero, out errPtr);

            if (rc != SQLITE_OK)
            {
                string message = $"SQLite 오류 (rc={rc})";
                if (errPtr != IntPtr.Zero)
                {
                    message += ": " + Marshal.PtrToStringAnsi(errPtr);
                    sqlite3_free(errPtr);
                }

                throw new InvalidOperationException(message);
            }
        }

        // case_info 테이블에 key/value 를 넣는 간단한 헬퍼.
        public static void InsertKeyValue(IntPtr db, string key, string value)
        {
            key = key.Replace("'", "''");
            value = value.Replace("'", "''");

            string sql = $"INSERT OR REPLACE INTO case_info(key, value) " +
                         $"VALUES('{key}', '{value}');";

            ExecNonQuery(db, sql);
        }
    }
}
