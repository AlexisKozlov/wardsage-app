// Public keys the update check trusts. The normal build trusts the main key only; the test build (TESTBUILD,
// src/Channel.cs) also trusts a separate key used for automatic test builds. Kept free of Windows-only code so the
// core tests cover it.
using System;
using System.Security.Cryptography;

namespace RealmForge {
  public static class UpdateKeys {
    // main key (base64url), from tools/update-keygen.mjs
    public const string KeyN = "6MiNF84wJ_zDGNfzUv5x11fzdDrgLEBJsZUvjuhpFYv90wVNCUbKTU7FkmGcFFz-n0SUx6n1dkVwkc98JB8qzA_2SLTBtX_WqdqFBOzm4dQubRzxkPjOWVP0i2OYZYou2G4txHDNcOi66yZXfVw1-HhtjB7osxEAKR62O2gVBx3_GKUQs_Pq08PPDx-jBvaBqt4Yrr5UoMPypU7OZKQzVv_NQ3aOV9s_8_bn5TWygaez5hgAGipdciZZ91FTOStk_yLF0RanB2CFbGQIhZckGI0HefEy7aw_LlsOkWcFOYG4QRDMgWfW1kNGrqOCETfBwXPM0Krb3jT4mXTO4pL2kbC_MYXo2vwcyi8BPZZfOtNu4DCJ0D0GXpTy4zYk8cvBhTHoY9-_xKMfoI3eMLW5WOUFzBrzNiz-oLfPsGsntbZmo1FgJDUyKI21FIc0wVCjVCZ-GkstjizfzxIeMoQMFEh3tSHn7bPmeE2L5zmDHuGIm2JgTRzfmy4DcPZQV0Zj";
    // key of the automatic test builds: trusted by the test build only
    public const string TestKeyN = "9ZmiNDqwjh93pt2GrIE-8gIB0BxH08z8XglJqknEK3LTAoEE_f0gJtqbk2le-OB2iv2R8WZboGR7bYAKa0Mt9SGt8ZtuMRnLJNnj-0K1pj7PPvAUfRhEVwZwKhqXNQEvfU8HWkuCOmkVP04OrHkhJXTILwJmbP2nZsgFjRzheaVeVb3rnkDkhTBxbcTh-Xjjqc2RqIBjw_C3JemBJhWI_ngcbY2H8hE60R6S9MIiyrxqJZBBTJrhB57vsyhWygPL1-rONNYQPtnqXlhhH1RA9c1oP2x983MbAJQqYrBp7HDnnRqXmXjv2pTfSySQsVfnyawRwM3ws4_OLg7x7Htfa33efo_sqeKSjXwcPu7CjkXDoZZmSvATwUBGNlDotubqdbRW9ylSuDYg4MgXOGBI-SRi-9Id8GIjIk1oai8auadnuNdTp4gHnfQ4tUtt4Ya1zql8BND3KB2w9-wszH9gM_4w59Mp3Ka3W2JiL6AeajpLZyAngtEL_1rpb8fAhw4r";
    public const string KeyE = "AQAB";

    /// <summary>Moduli (base64url) accepted for the given channel.</summary>
    public static string[] Accepted(bool isTest) {
      return isTest ? new[] { KeyN, TestKeyN } : new[] { KeyN };
    }

    static byte[] B64Url(string s) {
      s = s.Replace('-', '+').Replace('_', '/');
      while (s.Length % 4 != 0) s += "=";
      return Convert.FromBase64String(s);
    }

    /// <summary>RSA PKCS#1 v1.5 / SHA-256 signature check against any of the given moduli.</summary>
    public static bool VerifyAny(byte[] data, byte[] sig, string[] moduli) {
      foreach (var n in moduli) {
        try {
          using (var rsa = RSA.Create()) {
            rsa.ImportParameters(new RSAParameters { Modulus = B64Url(n), Exponent = B64Url(KeyE) });
            if (rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return true;
          }
        } catch (Exception) { }
      }
      return false;
    }
  }
}
