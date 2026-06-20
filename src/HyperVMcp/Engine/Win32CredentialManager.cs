// Copyright (c) McpSharp contributors
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;

namespace HyperVMcp.Engine;

/// <summary>
/// Minimal P/Invoke wrapper around the Win32 Credential Manager read API
/// (advapi32!CredReadW). Reads Generic credentials — the same store that
/// <c>cmdkey /generic:</c> and the Credential Manager UI write to — without
/// depending on any PowerShell module or third-party package.
/// </summary>
internal static class Win32CredentialManager
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    /// <summary>
    /// Read a Generic credential by target name. Returns null if no such
    /// credential exists in the current user's Credential Manager store.
    /// </summary>
    public static StoredCredential? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var handle))
        {
            // ERROR_NOT_FOUND is the expected "no such credential" case; surface
            // any other failure (e.g. access denied) instead of masking it as null.
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
                return null;
            throw new System.ComponentModel.Win32Exception(
                error, $"CredRead failed for target '{target}' (Win32 error {error}).");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle);

            var userName = cred.UserName == IntPtr.Zero
                ? null
                : Marshal.PtrToStringUni(cred.UserName);

            string? password = null;
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0)
            {
                // CredentialBlobSize is a byte count; the blob is a UTF-16 string.
                password = Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            }

            return new StoredCredential(userName, password);
        }
        finally
        {
            CredFree(handle);
        }
    }
}

/// <summary>A credential read from the Windows Credential Manager.</summary>
internal sealed record StoredCredential(string? UserName, string? Password)
{
    // Override the record's auto-generated ToString so the password is never
    // emitted if a StoredCredential is accidentally logged or interpolated.
    public override string ToString() => $"StoredCredential {{ UserName = {UserName} }}";
}
