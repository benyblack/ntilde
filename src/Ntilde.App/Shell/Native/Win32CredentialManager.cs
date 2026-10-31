using Ntilde.Platform.Security;
using System.Security.Cryptography;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ntilde.Shell.Native
{
    public static class Win32CredentialManager
    {
        private const string DllName = "advapi32.dll";

        [DllImport(DllName, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

        [DllImport(DllName, EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, CREDENTIAL_TYPE type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport(DllName, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredDelete(string target, CREDENTIAL_TYPE type, int flags);

        [DllImport(DllName, SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        private enum CREDENTIAL_TYPE : uint
        {
            GENERIC = 1,
            DOMAIN_PASSWORD = 2,
            DOMAIN_VISIBLE_PASSWORD = 3
        }

        private enum CREDENTIAL_PERSIST : uint
        {
            SESSION = 1,
            LOCAL_MACHINE = 2,
            ENTERPRISE = 3
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        public static bool Write(string target, string username, string password)
        {
            using var secret = SecretBuffer.FromText(password);
            return WriteBuffer(target, username, secret);
        }

        public static bool WriteBuffer(string target, string username, SecretBuffer password)
        {
            CREDENTIAL credential = default;
            int charCount = Encoding.UTF8.GetCharCount(password.Span);
            char[] blob = GC.AllocateArray<char>(charCount + 1, pinned: true);
            try
            {
                Encoding.UTF8.GetChars(password.Span, blob);
                credential.Type = (uint)CREDENTIAL_TYPE.GENERIC;
                credential.TargetName = Marshal.StringToCoTaskMemUni(target);
                credential.UserName = Marshal.StringToCoTaskMemUni(username);
                credential.Persist = (uint)CREDENTIAL_PERSIST.LOCAL_MACHINE;
                credential.CredentialBlobSize = checked((uint)(charCount * sizeof(char)));
                credential.CredentialBlob = Marshal.UnsafeAddrOfPinnedArrayElement(blob, 0);
                return CredWrite(ref credential, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
                Marshal.FreeCoTaskMem(credential.UserName);
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(blob.AsSpan()));
                GC.KeepAlive(blob);
            }
        }

        public static (string Username, string Password)? Read(string target)
        {
            var result = ReadBuffer(target);
            if (result is null) return null;
            using var secret = result.Value.Password;
            return (result.Value.Username, secret.DangerousGetString());
        }

        public static (string Username, SecretBuffer Password)? ReadBuffer(string target)
        {
            if (!CredRead(target, CREDENTIAL_TYPE.GENERIC, 0, out IntPtr pCred)) return null;
            CREDENTIAL credential = default;
            try
            {
                credential = Marshal.PtrToStructure<CREDENTIAL>(pCred);
                string username = Marshal.PtrToStringUni(credential.UserName) ?? "";
                if (credential.CredentialBlobSize % sizeof(char) != 0) throw new InvalidOperationException("Invalid credential encoding.");
                char[] chars = GC.AllocateArray<char>(checked((int)credential.CredentialBlobSize / sizeof(char)), pinned: true);
                try
                {
                    if (chars.Length > 0) Marshal.Copy(credential.CredentialBlob, chars, 0, chars.Length);
                    return (username, SecretBuffer.FromText(chars));
                }
                finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan())); }
            }
            finally
            {
                // CredRead allocated this process-local copy; CredFree alone does not promise a wipe.
                if (credential.CredentialBlob != IntPtr.Zero)
                    for (int index = 0; index < credential.CredentialBlobSize; index++) Marshal.WriteByte(credential.CredentialBlob, index, 0);
                CredFree(pCred);
            }
        }

        public static bool Delete(string target)
        {
            return CredDelete(target, CREDENTIAL_TYPE.GENERIC, 0);
        }
    }
}
