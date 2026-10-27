using Chispa.Release;

namespace Chispa.Launcher;

// The loader's trust anchors for release signatures.
//
// These are PUBLIC keys and they are compiled in on purpose. A client that read its trust anchors
// from the server, a file next to the executable, or an environment variable would be verifying
// nothing: whoever can swap the package can swap the key list too. Changing this list is therefore
// a client release, which is the cost - and the point - of the design.
//
// THE PRIVATE KEY IS NOT HERE AND MUST NOT BE. It lives only in the release environment, next to
// chispa-sign (tools/Chispa.Sign), and the server that hands out packages never has it either.
//
// ROTATION
//   1. In the release environment: chispa-sign keygen --out <dir> --id <new-id>.
//   2. Paste the printed line below, keeping the CURRENT key in the list as well.
//   3. Build and distribute a client that trusts both. Nothing breaks: old packages still verify.
//   4. Only after the new client is everywhere, sign new releases with the new key and remove the
//      old line in a later client build. A key removed from this list stops being accepted, and
//      packages signed only with it stop installing - which is the intended way to retire a key.
internal static class ReleaseTrust
{
    /// <summary>The built-in anchors. Order does not matter; ids must be unique.</summary>
    private static readonly TrustedReleaseKey[] BuiltIn =
    [
        new("release-dev-2026-10",
            "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAxo6wyjcjur4JZkA3hSU7UdRMYZyQOSyxxZiIUJXhMdGpSRwF7KilJMUitcRA0L1bclySqNYA5lb2oBwyE+zCaqKOONlmWiHpWx2EjXBQZ04zofiWuuSgX3Gh4lnlwfkvquQ+QJugjrnkpHIp/BWZVAjJWmQsLFUALH7qH0rLOLQBkfb2+2MZcbCy3sC8qwxXTzwb8FkDd4oqgjZWLUmqizizEJSvsUvIgKqn2obzu6dSzto4EQf5PdNuR2rdlaF+uClsMl6Bmv/0Q4hDVxG4BUobEN5eO4KGz0/UFZB3XUCMm1YJbYLakbVsz3Q/CbrWXt7b35ObVNog1DoGroPRdQIDAQAB"),
    ];

    public static IReadOnlyList<TrustedReleaseKey> Keys => BuiltIn;
}
