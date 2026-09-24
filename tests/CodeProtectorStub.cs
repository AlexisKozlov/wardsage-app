// Test-only stand-in for src/CodeProtector.cs: DPAPI (ProtectedData) is Windows-only and is not
// part of the .NET 8 reference pack. It "encrypts" by reversing + base64, which is enough to check
// that config.json never contains the code in plain text.
using System;
using System.Text;

namespace RealmForge {
  public static class CodeProtector {
    public static string Protect(string plain) {
      char[] a = plain.ToCharArray(); Array.Reverse(a);
      return Convert.ToBase64String(Encoding.UTF8.GetBytes(new string(a)));
    }
    public static string Unprotect(string b64) {
      char[] a = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).ToCharArray(); Array.Reverse(a);
      return new string(a);
    }
  }
}
