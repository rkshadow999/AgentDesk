using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace AgentDesk.Platform.Windows.Credentials;

internal sealed class WindowsCredentialApi : ICredentialApi
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumCredentialBlobSize = 5 * 512;

    public void Write(string target, string userName, byte[] secret)
    {
        if (secret.Length > MaximumCredentialBlobSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secret),
                $"Credential exceeds the Windows limit of {MaximumCredentialBlobSize} bytes.");
        }

        var targetPointer = Marshal.StringToHGlobalUni(target);
        var userPointer = Marshal.StringToHGlobalUni(userName);
        var secretPointer = Marshal.AllocHGlobal(secret.Length);

        try
        {
            Marshal.Copy(secret, 0, secretPointer, secret.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = checked((uint)secret.Length),
                CredentialBlob = secretPointer,
                Persist = CredentialPersistLocalMachine,
                UserName = userPointer,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw CreateException("write", target);
            }
        }
        finally
        {
            ZeroAndFree(secretPointer, secret.Length);
            Marshal.FreeHGlobal(userPointer);
            Marshal.FreeHGlobal(targetPointer);
        }
    }

    public byte[]? Read(string target)
    {
        if (!CredRead(target, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, $"Failed to read Windows credential '{target}'.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            var length = checked((int)credential.CredentialBlobSize);
            var secret = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(credential.CredentialBlob, secret, 0, length);
            }

            return secret;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public bool Delete(string target)
    {
        if (CredDelete(target, CredentialTypeGeneric, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, $"Failed to delete Windows credential '{target}'.");
    }

    private static Win32Exception CreateException(string operation, string target)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"Failed to {operation} Windows credential '{target}'.");
    }

    private static void ZeroAndFree(nint pointer, int length)
    {
        for (var index = 0; index < length; index++)
        {
            Marshal.WriteByte(pointer, index, 0);
        }

        Marshal.FreeHGlobal(pointer);
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out nint credentialPointer);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }
}
