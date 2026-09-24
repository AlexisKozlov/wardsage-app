// RealmForge extractor - encrypting the sync code with Windows DPAPI (current user).

using System;
using System.Security.Cryptography;
using System.Text;

namespace RealmForge {
  public static class CodeProtector {
    // Extra entropy: other programs of the same user cannot decrypt the blob by accident.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RealmForge.SyncCode.v1");

    public static string Protect(string plain) {
      byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
      return Convert.ToBase64String(data);
    }

    public static string Unprotect(string base64) {
      byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(base64), Entropy, DataProtectionScope.CurrentUser);
      return Encoding.UTF8.GetString(data);
    }
  }
}
