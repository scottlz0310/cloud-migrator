using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CloudMigrator.Core.Credentials;

/// <summary>
/// Windows Credential Manager（CredWrite / CredRead / CredDelete P/Invoke）を使用した
/// <see cref="ICredentialStore"/> 実装。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore : ICredentialStore
{
    // Credential の種別: 汎用資格情報
    private const uint CRED_TYPE_GENERIC = 1;

    // 永続化スコープ: ローカルマシン（ログインセッション間で保持）
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    // Win32 エラーコード: ERROR_NOT_FOUND
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredWriteW", CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredReadW", CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);

    /// <inheritdoc/>
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(key, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
                return Task.FromResult<string?>(null);
            throw new Win32Exception(error,
                $"Credential Manager からの読み取りに失敗しました: {key}（Win32 エラー {error}）");
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero)
                return Task.FromResult<string?>(null);

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);

            // UTF-8 でデコードを試みる（SaveAsync の新方式）。
            // 旧バージョンは Encoding.Unicode（UTF-16LE）で保存していたため、
            // UTF-8 デコード結果に \0 が含まれる場合は UTF-16LE へフォールバックする。
            var utf8Decoded = Encoding.UTF8.GetString(bytes);
            var value = utf8Decoded.Contains('\0')
                ? Encoding.Unicode.GetString(bytes).TrimEnd('\0')
                : utf8Decoded;
            return Task.FromResult<string?>(string.IsNullOrEmpty(value) ? null : value);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <inheritdoc/>
    public Task SaveAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();

        // ASCII トークン等は UTF-8 (1 バイト/文字) でエンコードすることで
        // CRED_TYPE_GENERIC のサイズ上限 (2560 バイト) 内に収める
        var blob = Encoding.UTF8.GetBytes(value);

        // CRED_TYPE_GENERIC の CredentialBlobSize 上限は 5 * 512 = 2560 バイト
        const int MaxBlobSize = 2560;
        if (blob.Length > MaxBlobSize)
            throw new InvalidOperationException(
                $"Credential Manager への書き込みに失敗しました: {key} の値が上限サイズ（{MaxBlobSize} バイト）を超えています（{blob.Length} バイト）。");

        var blobHandle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = key,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobHandle.AddrOfPinnedObject(),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                // CRED_TYPE_GENERIC では UserName は任意フィールド。
                // 環境によってはユーザー名の内容が CredWrite を失敗させるため省略する。
            };
            if (!CredWrite(ref cred, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error,
                    $"Credential Manager への書き込みに失敗しました: {key}（Win32 エラー {error}）");
            }
        }
        finally
        {
            blobHandle.Free();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(key, CRED_TYPE_GENERIC, 0, out var credPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
                return Task.FromResult(false);
            throw new Win32Exception(error,
                $"Credential Manager の存在確認に失敗しました: {key}（Win32 エラー {error}）");
        }

        CredFree(credPtr);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredDelete(key, CRED_TYPE_GENERIC, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_NOT_FOUND)
                return Task.CompletedTask; // 既に存在しない場合は正常終了
            throw new Win32Exception(error,
                $"Credential Manager からの削除に失敗しました: {key}（Win32 エラー {error}）");
        }

        return Task.CompletedTask;
    }
}
