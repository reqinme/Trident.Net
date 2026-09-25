using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TridentCore.Core.Utilities;

/// <summary>
///     Creates the links that project the shared cache and the persistence directory into a run
///     directory, and reports whether a path is one.
/// </summary>
/// <remarks>
///     Windows refuses to create a symbolic link unless the process holds
///     SeCreateSymbolicLinkPrivilege, which an ordinary unelevated process only has through Windows
///     Developer Mode. Deployment must keep working without it, so a link that the OS rejects falls
///     back to a hard link for a file and to a directory junction for a directory: neither needs a
///     privilege. Both remain disposable - removing one drops a directory entry and never destroys
///     content, because the cache and the persistence directory keep their own name for the file.
///     The fallback is Windows-only; symlink(2) needs no privilege, so the primary path always
///     succeeds elsewhere.
/// </remarks>
public static class ProjectionLinkHelper
{
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const string NtPathPrefix = @"\??\";

    /// <summary>
    ///     Links <paramref name="path" /> to <paramref name="target" />, preferring a symbolic link
    ///     and falling back to the unprivileged equivalent when the OS rejects one. The caller is
    ///     responsible for ensuring <paramref name="path" /> does not already exist.
    /// </summary>
    public static void Create(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            return;
        }
        catch (Exception exception) when (OperatingSystem.IsWindows()
                                          && exception is UnauthorizedAccessException or IOException)
        {
            // Fall through: the privilege is missing, not the path.
        }

        if (directory) CreateJunction(path, target);
        else CreateHardLink(path, target);
    }

    /// <summary>
    ///     Whether <paramref name="path" /> is a hard link to content that also has another name.
    /// </summary>
    /// <remarks>
    ///     Only files are considered. A directory reports a link count that covers its subdirectories
    ///     rather than additional names, so treating it as a hard link would make every non-empty
    ///     directory look disposable. Always false off Windows, where this fallback is never used.
    /// </remarks>
    public static bool IsHardLink(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!File.Exists(path)) return false;
        return TryReadFileInformation(path, out var information) && information.NumberOfLinks > 1;
    }

    /// <summary>
    ///     Whether <paramref name="path" /> and <paramref name="other" /> are two names for the same
    ///     content. A hard link records no target, so identity - not a path - is what proves a
    ///     projection is already in place.
    /// </summary>
    public static bool SharesContent(string path, string other)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!TryReadFileInformation(path, out var left)) return false;
        if (!TryReadFileInformation(other, out var right)) return false;
        return left.VolumeSerialNumber == right.VolumeSerialNumber
               && left.FileIndexHigh == right.FileIndexHigh
               && left.FileIndexLow == right.FileIndexLow;
    }

    private static void CreateHardLink(string path, string target)
    {
        if (CreateHardLinkNative(path, target, IntPtr.Zero)) return;
        throw new IOException($"Cannot link the projection {path} to {target}: " + DescribeLastError());
    }

    private static string DescribeLastError()
    {
        var error = Marshal.GetLastWin32Error();
        return $"Win32 {error} ({new Win32Exception(error).Message})";
    }

    // A junction is a mount-point reparse point: it needs no privilege, unlike a directory symbolic
    // link, and it keeps whole-directory persistence working - the game writing a new file under a
    // projected `.keep` directory still lands in the persistence directory rather than in the run
    // directory, which projecting the directory file by file would silently stop doing.
    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        var full = Path.GetFullPath(target);
        var substitute = System.Text.Encoding.Unicode.GetBytes(NtPathPrefix + full);
        var print = System.Text.Encoding.Unicode.GetBytes(full);
        // REPARSE_DATA_BUFFER: an 8 byte header, then the mount point payload - four offsets, the
        // substitute name, the display name - each name closed by a UTF-16 null that the name
        // lengths exclude. The buffer is assembled by hand because the payload is variable length
        // and the kernel checks every declared length against the buffer size it is handed.
        var payload = 8 + substitute.Length + 2 + print.Length + 2;
        var buffer = new byte[8 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), IO_REPARSE_TAG_MOUNT_POINT);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)payload);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer.AsSpan(16));
        print.CopyTo(buffer.AsSpan(16 + substitute.Length + 2));

        var pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, pointer, buffer.Length);
            using var handle = CreateFileNative(path, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                                                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
            if (handle.IsInvalid)
                throw new IOException($"Cannot open the projection directory {path}: " + DescribeLastError());
            if (DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, pointer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) return;
            throw new IOException($"Cannot create the junction {path} to {target}: " + DescribeLastError());
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static bool TryReadFileInformation(string path, out ByHandleFileInformation information)
    {
        information = default;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(handle, out information);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkNative(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileNative(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                                          uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer, int inBufferSize,
                                               IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
