using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AppDock.Core.Interop;

namespace AppDock.Core.Updates;

/// <summary>Checks a downloaded installer before it is run without asking anyone.</summary>
public static class PackageVerifier
{
    private const int TrustOk = 0;

    // The chain ends in a root this PC does not trust, but the file's hash and signature did verify. Expected: builds
    // use a self-signed certificate that users' PCs do not trust, so trust comes from matching the signer instead.
    private const int CertUntrustedRoot = unchecked((int)0x800B0109);

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>True if both files are signed by the same certificate and <paramref name="path"/> is unmodified since signing.</summary>
    public static bool IsSignedBySameCertificate(string path, string referencePath)
    {
        var expected = SignerThumbprint(referencePath);
        return expected is not null &&
               string.Equals(SignerThumbprint(path), expected, StringComparison.Ordinal) &&
               HasIntactSignature(path);
    }

    public static string? SignerThumbprint(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // X509CertificateLoader cannot read Authenticode signatures.
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return signer.GetCertHashString(HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return null; // not signed
        }
    }

    private static unsafe bool HasIntactSignature(string path)
    {
        fixed (char* filePath = path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = (nint)filePath,
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = Native.WTD_UI_NONE,
                fdwRevocationChecks = Native.WTD_REVOKE_NONE,
                dwUnionChoice = Native.WTD_CHOICE_FILE,
                pFile = (nint)(&file),
                dwStateAction = Native.WTD_STATEACTION_VERIFY,
                dwProvFlags = Native.WTD_REVOCATION_CHECK_NONE,
            };

            var action = Native.WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var result = Native.WinVerifyTrust(0, ref action, ref data);
            data.dwStateAction = Native.WTD_STATEACTION_CLOSE;
            _ = Native.WinVerifyTrust(0, ref action, ref data);
            return result is TrustOk or CertUntrustedRoot;
        }
    }
}
