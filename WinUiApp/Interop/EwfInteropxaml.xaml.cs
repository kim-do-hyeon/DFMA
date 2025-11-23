using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinUiApp.Services
{
    /// <summary>
    /// libewf DLL 핸들을 감싸는 SafeHandle
    /// </summary>
    public sealed class EwfLibraryHandle : SafeHandle
    {
        // SafeHandle 기본 생성자 호출 (ownsHandle = true)
        private EwfLibraryHandle() : base(IntPtr.Zero, ownsHandle: true) { }

        internal EwfLibraryHandle(IntPtr existingHandle) : this()
        {
            SetHandle(existingHandle);
        }

        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle()
        {
            if (!IsInvalid)
            {
                try
                {
                    NativeLibrary.Free(handle);
                }
                catch
                {
                    return false;
                }
                finally
                {
                    handle = IntPtr.Zero;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// NativeLibrary.Load / GetExport 래퍼 (이름 충돌 피하려고 Ewf 붙임)
    /// </summary>
    public static class EwfNativeLibraryLoader
    {
        public static EwfLibraryHandle Load(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                throw new ArgumentException("DLL 경로가 비어 있습니다.", nameof(fullPath));

            IntPtr h = NativeLibrary.Load(fullPath);
            return new EwfLibraryHandle(h);
        }

        public static IntPtr GetExport(EwfLibraryHandle lib, string name)
        {
            if (lib is null || lib.IsInvalid)
                throw new ArgumentException("잘못된 라이브러리 핸들");

            if (!NativeLibrary.TryGetExport(lib.DangerousGetHandle(), name, out IntPtr pFn) || pFn == IntPtr.Zero)
                throw new EntryPointNotFoundException($"엔트리 포인트를 찾을 수 없습니다: {name}");

            return pFn;
        }
    }

    /// <summary>
    /// libewf P/Invoke (필요 최소 함수만)
    /// </summary>
    public static class EwfNativeAdvanced
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int libewf_handle_initialize_delegate(out IntPtr handle, IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int libewf_handle_open_wide_delegate(
            IntPtr handle,
            IntPtr filenames /* wchar_t** */,
            int number_of_filenames,
            int access_flags,
            IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int libewf_handle_close_delegate(IntPtr handle, IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int libewf_handle_free_delegate(ref IntPtr handle, IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int libewf_handle_get_media_size_delegate(
            IntPtr handle, out long media_size, IntPtr error);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr libewf_handle_read_random_delegate(
            IntPtr handle, IntPtr buffer, UIntPtr buffer_size, long offset, IntPtr error);

        private static T Load<T>(EwfLibraryHandle lib, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(EwfNativeLibraryLoader.GetExport(lib, name));

        public static IntPtr HandleInit(EwfLibraryHandle lib)
        {
            var fn = Load<libewf_handle_initialize_delegate>(lib, "libewf_handle_initialize");
            if (fn(out var h, IntPtr.Zero) != 1 || h == IntPtr.Zero)
                throw new InvalidOperationException("libewf_handle_initialize 실패");
            return h;
        }

        public static void HandleOpenWide(EwfLibraryHandle lib, IntPtr handle, string e01Path)
        {
            var arrPtr = Marshal.AllocHGlobal(IntPtr.Size);
            var strPtr = Marshal.StringToHGlobalUni(e01Path);
            try
            {
                Marshal.WriteIntPtr(arrPtr, strPtr);
                var open = Load<libewf_handle_open_wide_delegate>(lib, "libewf_handle_open_wide");
                const int LIBEWF_OPEN_READ = 1;
                if (open(handle, arrPtr, 1, LIBEWF_OPEN_READ, IntPtr.Zero) != 1)
                    throw new InvalidOperationException("libewf_handle_open_wide 실패");
            }
            finally
            {
                Marshal.FreeHGlobal(strPtr);
                Marshal.FreeHGlobal(arrPtr);
            }
        }

        public static long GetMediaSize(EwfLibraryHandle lib, IntPtr handle)
        {
            var fn = Load<libewf_handle_get_media_size_delegate>(lib, "libewf_handle_get_media_size");
            if (fn(handle, out long size, IntPtr.Zero) != 1 || size <= 0)
                throw new InvalidOperationException("libewf_handle_get_media_size 실패");
            return size;
        }

        public static int ReadAt(EwfLibraryHandle lib, IntPtr handle, IntPtr buffer, int size, long offset)
        {
            var fn = Load<libewf_handle_read_random_delegate>(lib, "libewf_handle_read_random");
            var ret = fn(handle, buffer, (UIntPtr)(ulong)size, offset, IntPtr.Zero);
            long n = ret.ToInt64();
            if (n < 0) return -1;
            if (n > int.MaxValue) n = int.MaxValue;
            return (int)n;
        }

        public static void HandleCloseFree(EwfLibraryHandle lib, IntPtr handle)
        {
            try
            {
                var close = Load<libewf_handle_close_delegate>(lib, "libewf_handle_close");
                close(handle, IntPtr.Zero);
            }
            finally
            {
                var free = Load<libewf_handle_free_delegate>(lib, "libewf_handle_free");
                var h = handle;
                free(ref h, IntPtr.Zero);
            }
        }
    }

    /// <summary>
    /// EWF(handle) → random-access Stream
    /// </summary>
    public sealed class EwfStream : Stream
    {
        private readonly EwfLibraryHandle _lib;
        private IntPtr _handle;
        private readonly long _length;
        private long _position;

        public EwfStream(EwfLibraryHandle lib, IntPtr handle, long length)
        {
            _lib = lib;
            _handle = handle;
            _length = length;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (count == 0) return 0;

            IntPtr ptr = Marshal.AllocHGlobal(count);
            try
            {
                int read = EwfNativeAdvanced.ReadAt(_lib, _handle, ptr, count, _position);
                if (read > 0)
                {
                    Marshal.Copy(ptr, buffer, offset, read);
                    _position += read;
                }
                return read < 0 ? 0 : read;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => _position
            };

            if (newPos < 0) newPos = 0;
            if (newPos > _length) newPos = _length;

            _position = newPos;
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (_handle != IntPtr.Zero)
            {
                var h = _handle;
                _handle = IntPtr.Zero;
                EwfNativeAdvanced.HandleCloseFree(_lib, h);
            }
            base.Dispose(disposing);
        }
    }
}
