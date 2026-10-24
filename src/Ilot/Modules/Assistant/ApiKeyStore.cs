using System.Runtime.InteropServices;
using System.Text;
using Ilot.Interop;

namespace Ilot.Modules.Assistant;

/// <summary>
/// The Gemini API key, kept by Windows in its Credential Manager (encrypted for this user), never in settings.json.
/// </summary>
public sealed class ApiKeyStore
{
    private const string Target = "MyNotch/Gemini"; // Its name from before the app was renamed Ilot: the key saved stays found.

    private string? _cached;
    private bool _read;

    /// <summary>The key, or <c>null</c> if none was saved.</summary>
    public string? Key
    {
        get
        {
            if (!_read)
            {
                _cached = Read();
                _read = true;
            }

            return _cached;
        }
    }

    public bool HasKey => !string.IsNullOrWhiteSpace(Key);

    /// <summary>Raised when the key is saved or removed.</summary>
    public event EventHandler? Changed;

    /// <summary>Saves <paramref name="key"/> (an empty one removes it).</summary>
    public bool Save(string? key)
    {
        key = key?.Trim();
        bool done = string.IsNullOrEmpty(key) ? Remove() : Write(key);
        if (done)
        {
            _cached = string.IsNullOrEmpty(key) ? null : key;
            _read = true;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return done;
    }

    private static string? Read()
    {
        if (!NativeMethods.CredRead(Target, NativeMethods.CRED_TYPE_GENERIC, 0, out nint pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.Credential>(pointer);
            return credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / sizeof(char));
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    private static bool Write(string key)
    {
        byte[] secret = Encoding.Unicode.GetBytes(key);
        nint target = Marshal.StringToHGlobalUni(Target);
        nint blob = Marshal.AllocHGlobal(secret.Length);
        try
        {
            Marshal.Copy(secret, 0, blob, secret.Length);
            var credential = new NativeMethods.Credential
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlobSize = (uint)secret.Length,
                CredentialBlob = blob,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
            };
            return NativeMethods.CredWrite(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static bool Remove() =>
        NativeMethods.CredDelete(Target, NativeMethods.CRED_TYPE_GENERIC, 0) || Marshal.GetLastPInvokeError() == 1168; // Not found: gone already.
}
