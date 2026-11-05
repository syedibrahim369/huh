using System.Collections.Concurrent;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography.Catalog;
using Windows.Win32.Security.WinTrust;

namespace Huh.Core.Provenance;

public enum SignatureState
{
    /// <summary>Valid Authenticode signature chaining to a trusted root (embedded or via a Windows catalog).</summary>
    Signed,
    /// <summary>No signature at all.</summary>
    Unsigned,
    /// <summary>Signed, but the signature doesn't verify (tampered, untrusted root, explicitly distrusted...).</summary>
    Invalid,
    /// <summary>Couldn't check (no path: protected process, file gone).</summary>
    Unknown,
}

/// <param name="Signer">Subject display name of the leaf signing certificate, e.g. "Microsoft Windows", "Google LLC".</param>
/// <param name="ViaCatalog">True when the file is signed through a Windows catalog (.cat) instead of an embedded signature.</param>
/// <param name="Detail">Human-readable reason for Invalid/Unknown.</param>
public sealed record SignatureInfo(SignatureState State, string? Signer = null, bool ViaCatalog = false, string? Detail = null);

/// <summary>
/// Offline Authenticode verification with WinVerifyTrust (no UI, no revocation network calls).
/// Checks the embedded signature first, then Windows catalogs (most in-box binaries are catalog-signed).
/// </summary>
public static unsafe class Authenticode
{
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;

    private static readonly ConcurrentDictionary<(string, DateTime, long), SignatureInfo> Cache = new();

    public static SignatureInfo Verify(string? path)
    {
        if (string.IsNullOrEmpty(path)) return new SignatureInfo(SignatureState.Unknown, Detail: "no file path");
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return new SignatureInfo(SignatureState.Unknown, Detail: "file not found"); }
        catch (Exception ex) { return new SignatureInfo(SignatureState.Unknown, Detail: ex.GetType().Name); }

        return Cache.GetOrAdd((fi.FullName.ToUpperInvariant(), fi.LastWriteTimeUtc, fi.Length), _ => VerifyUncached(fi.FullName));
    }

    private static SignatureInfo VerifyUncached(string path)
    {
        var (hr, signer) = VerifyEmbedded(path);
        // On current Windows, file-mode WinVerifyTrust also validates catalog-signed files by itself, so "how" is decided
        // by whether the PE actually carries a certificate table.
        if (hr == 0) return new SignatureInfo(SignatureState.Signed, signer, ViaCatalog: !HasEmbeddedSignature(path));

        if (hr is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
        {
            var cat = VerifyCatalog(path);
            if (cat is { } c)
                return c.Hr == 0
                    ? new SignatureInfo(SignatureState.Signed, c.Signer, ViaCatalog: true)
                    : new SignatureInfo(SignatureState.Invalid, c.Signer, true, Describe(c.Hr));
            return new SignatureInfo(SignatureState.Unsigned);
        }
        return new SignatureInfo(SignatureState.Invalid, signer, Detail: Describe(hr));
    }

    private static (int Hr, string? Signer) VerifyEmbedded(string path)
    {
        fixed (char* p = path)
        {
            var file = new WINTRUST_FILE_INFO { cbStruct = (uint)sizeof(WINTRUST_FILE_INFO), pcwszFilePath = p };
            var data = NewData(WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_FILE);
            data.Anonymous.pFile = &file;
            return Run(ref data);
        }
    }

    private static (int Hr, string? Signer)? VerifyCatalog(string path)
    {
        byte* hash = stackalloc byte[128];
        foreach (var alg in (ReadOnlySpan<string>)["SHA256", "SHA1"])
        {
            nint admin;
            fixed (char* a = alg)
                if (!PInvoke.CryptCATAdminAcquireContext2(&admin, null, a, null, 0)) continue;
            try
            {
                using var fh = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                uint cb = 0;
                PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, (HANDLE)fh.DangerousGetHandle(), &cb, null, 0);
                if (cb == 0 || cb > 128) continue;
                // (hash buffer is allocated once, above the loop)
                if (!PInvoke.CryptCATAdminCalcHashFromFileHandle2(admin, (HANDLE)fh.DangerousGetHandle(), &cb, hash, 0)) continue;

                nint catInfo = PInvoke.CryptCATAdminEnumCatalogFromHash(admin, hash, cb, 0, null);
                if (catInfo == 0) continue;
                try
                {
                    var ci = new CATALOG_INFO { cbStruct = (uint)sizeof(CATALOG_INFO) };
                    if (!PInvoke.CryptCATCatalogInfoFromContext(catInfo, &ci, 0)) continue;
                    string catalogFile = ci.wszCatalogFile.ToString();
                    string tag = Convert.ToHexString(new ReadOnlySpan<byte>(hash, (int)cb));

                    fixed (char* catPath = catalogFile)
                    fixed (char* member = path)
                    fixed (char* memberTag = tag)
                    {
                        var cat = new WINTRUST_CATALOG_INFO
                        {
                            cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                            pcwszCatalogFilePath = catPath,
                            pcwszMemberFilePath = member,
                            pcwszMemberTag = memberTag,
                            pbCalculatedFileHash = hash,
                            cbCalculatedFileHash = cb,
                            hCatAdmin = admin,
                        };
                        var data = NewData(WINTRUST_DATA_UNION_CHOICE.WTD_CHOICE_CATALOG);
                        data.Anonymous.pCatalog = &cat;
                        return Run(ref data);
                    }
                }
                finally { PInvoke.CryptCATAdminReleaseCatalogContext(admin, catInfo, 0); }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            finally { PInvoke.CryptCATAdminReleaseContext(admin, 0); }
        }
        return null;
    }

    /// <summary>True if the PE's IMAGE_DIRECTORY_ENTRY_SECURITY (certificate table) is non-empty.</summary>
    internal static bool HasEmbeddedSignature(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return false;          // "MZ"
            fs.Position = 0x3C;
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 0x100) return false;
            fs.Position = peOffset;
            if (br.ReadUInt32() != 0x00004550) return false;                            // "PE\0\0"
            fs.Position = peOffset + 24;                                               // optional header
            ushort magic = br.ReadUInt16();
            int dirOffset = magic switch { 0x10B => 96, 0x20B => 112, _ => -1 };      // PE32 / PE32+
            if (dirOffset < 0) return false;
            fs.Position = peOffset + 24 + dirOffset + 4 * 8;                           // data directory #4: security
            br.ReadUInt32();                                                           // file offset
            return br.ReadUInt32() > 0;                                                // size
        }
        catch { return false; }
    }

    private static WINTRUST_DATA NewData(WINTRUST_DATA_UNION_CHOICE choice) => new()
    {
        cbStruct = (uint)sizeof(WINTRUST_DATA),
        dwUIChoice = WINTRUST_DATA_UICHOICE.WTD_UI_NONE,
        fdwRevocationChecks = WINTRUST_DATA_REVOCATION_CHECKS.WTD_REVOKE_NONE,
        dwUnionChoice = choice,
        dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_VERIFY,
        // Offline: never hit the network for revocation/AIA (fast, private, works without internet).
        dwProvFlags = WINTRUST_DATA_PROVIDER_FLAGS.WTD_CACHE_ONLY_URL_RETRIEVAL | WINTRUST_DATA_PROVIDER_FLAGS.WTD_REVOCATION_CHECK_NONE,
    };

    private static (int Hr, string? Signer) Run(ref WINTRUST_DATA data)
    {
        var action = PInvoke.WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var noUi = new HWND(-1); // INVALID_HANDLE_VALUE: no UI
        int hr;
        string? signer;
        fixed (WINTRUST_DATA* d = &data)
        {
            hr = PInvoke.WinVerifyTrust(noUi, &action, d);
            signer = SignerName(d->hWVTStateData);
            d->dwStateAction = WINTRUST_DATA_STATE_ACTION.WTD_STATEACTION_CLOSE;
            PInvoke.WinVerifyTrust(noUi, &action, d);
        }
        return (hr, signer);
    }

    private static string? SignerName(HANDLE state)
    {
        if (state.IsNull) return null;
        var prov = PInvoke.WTHelperProvDataFromStateData(state);
        if (prov is null) return null;
        var sgnr = PInvoke.WTHelperGetProvSignerFromChain(prov, 0, false, 0);
        if (sgnr is null || sgnr->csCertChain == 0 || sgnr->pasCertChain is null || sgnr->pasCertChain[0].pCert is null) return null;
        char* buf = stackalloc char[256];
        uint n = PInvoke.CertGetNameString(sgnr->pasCertChain[0].pCert, CERT_NAME_SIMPLE_DISPLAY_TYPE, 0, null, new PWSTR(buf), 256);
        return n > 1 ? new string(buf, 0, (int)n - 1) : null;
    }

    private static string Describe(int hr) => (uint)hr switch
    {
        0x800B0109 => "certificate chain ends in an untrusted root",
        0x800B0101 => "signing certificate expired",
        0x800B010C => "certificate was revoked",
        0x800B0111 => "publisher explicitly distrusted",
        0x80096010 => "file was modified after it was signed",
        0x800B0004 => "subject not trusted",
        _ => $"verification failed (0x{hr:X8})",
    };
}
