// RealmForge extractor - a tiny JSON reader/writer.
//
// .NET Framework has no JSON parser in the assemblies that Add-Type references by default, and the
// extractor must not depend on anything that is not already on a Windows machine. This one is small
// and strict: it is used for server replies, config.json and for checking our own account.json.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RealmForge {
  public static class MiniJson {
    // Returns Dictionary<string, object>, List<object>, string, double, bool or null.
    // Throws FormatException on malformed input.
    public static object Parse(string text) {
      if (text == null) throw new FormatException("empty");
      var p = new Parser(text);
      p.Ws();
      if (p.Pos < text.Length && text[p.Pos] == '﻿') { p.Pos++; p.Ws(); }
      object v = p.Value(0);
      p.Ws();
      if (p.Pos != text.Length) throw new FormatException("trailing data at " + p.Pos);
      return v;
    }

    // Like Parse, but returns null instead of throwing.
    public static object TryParse(string text) {
      try { return Parse(text); } catch (FormatException) { return null; }
    }

    public static string Quote(string s) {
      var sb = new StringBuilder(s.Length + 2);
      sb.Append('"');
      foreach (char c in s) {
        switch (c) {
          case '"': sb.Append("\\\""); break;
          case '\\': sb.Append("\\\\"); break;
          case '\n': sb.Append("\\n"); break;
          case '\r': sb.Append("\\r"); break;
          case '\t': sb.Append("\\t"); break;
          default:
            if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4")); else sb.Append(c);
            break;
        }
      }
      sb.Append('"');
      return sb.ToString();
    }

    // --- helpers for reading parsed values ---
    public static Dictionary<string, object> AsObject(object o) { return o as Dictionary<string, object>; }

    public static string GetString(Dictionary<string, object> d, string key) {
      object v; if (d == null || !d.TryGetValue(key, out v)) return null;
      return v as string;
    }

    public static bool GetBool(Dictionary<string, object> d, string key, bool fallback) {
      object v; if (d == null || !d.TryGetValue(key, out v) || !(v is bool)) return fallback;
      return (bool)v;
    }

    // Returns -1 when the key is missing or not a non-negative whole number.
    public static int GetCount(Dictionary<string, object> d, string key) {
      object v; if (d == null || !d.TryGetValue(key, out v) || !(v is double)) return -1;
      double x = (double)v;
      if (x < 0 || x > int.MaxValue || Math.Floor(x) != x) return -1;
      return (int)x;
    }

    // Number of entries of an array or object value; -1 when missing.
    public static int CountOf(Dictionary<string, object> d, string key) {
      object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return -1;
      var list = v as List<object>; if (list != null) return list.Count;
      var obj = v as Dictionary<string, object>; if (obj != null) return obj.Count;
      return -1;
    }

    sealed class Parser {
      const int MaxDepth = 256;
      readonly string s;
      public int Pos;
      public Parser(string text) { s = text; }

      public void Ws() {
        while (Pos < s.Length) {
          char c = s[Pos];
          if (c == ' ' || c == '\t' || c == '\n' || c == '\r') Pos++; else break;
        }
      }

      FormatException Err(string what) { return new FormatException(what + " at " + Pos); }

      public object Value(int depth) {
        if (depth > MaxDepth) throw Err("nesting too deep");
        if (Pos >= s.Length) throw Err("unexpected end");
        char c = s[Pos];
        if (c == '{') return Obj(depth);
        if (c == '[') return Arr(depth);
        if (c == '"') return Str();
        if (c == 't') { Lit("true"); return true; }
        if (c == 'f') { Lit("false"); return false; }
        if (c == 'n') { Lit("null"); return null; }
        if (c == '-' || (c >= '0' && c <= '9')) return Num();
        throw Err("unexpected '" + c + "'");
      }

      void Lit(string word) {
        if (string.CompareOrdinal(s, Pos, word, 0, word.Length) != 0) throw Err("bad literal");
        Pos += word.Length;
      }

      Dictionary<string, object> Obj(int depth) {
        var d = new Dictionary<string, object>();
        Pos++; Ws();
        if (Pos < s.Length && s[Pos] == '}') { Pos++; return d; }
        while (true) {
          Ws();
          if (Pos >= s.Length || s[Pos] != '"') throw Err("expected key");
          string k = Str(); Ws();
          if (Pos >= s.Length || s[Pos] != ':') throw Err("expected ':'");
          Pos++; Ws();
          d[k] = Value(depth + 1); Ws();
          if (Pos >= s.Length) throw Err("unexpected end");
          if (s[Pos] == ',') { Pos++; continue; }
          if (s[Pos] == '}') { Pos++; return d; }
          throw Err("expected ',' or '}'");
        }
      }

      List<object> Arr(int depth) {
        var a = new List<object>();
        Pos++; Ws();
        if (Pos < s.Length && s[Pos] == ']') { Pos++; return a; }
        while (true) {
          Ws();
          a.Add(Value(depth + 1)); Ws();
          if (Pos >= s.Length) throw Err("unexpected end");
          if (s[Pos] == ',') { Pos++; continue; }
          if (s[Pos] == ']') { Pos++; return a; }
          throw Err("expected ',' or ']'");
        }
      }

      string Str() {
        Pos++; // opening quote
        var sb = new StringBuilder();
        while (true) {
          if (Pos >= s.Length) throw Err("unterminated string");
          char c = s[Pos++];
          if (c == '"') return sb.ToString();
          if (c != '\\') { sb.Append(c); continue; }
          if (Pos >= s.Length) throw Err("bad escape");
          char e = s[Pos++];
          switch (e) {
            case '"': sb.Append('"'); break;
            case '\\': sb.Append('\\'); break;
            case '/': sb.Append('/'); break;
            case 'b': sb.Append('\b'); break;
            case 'f': sb.Append('\f'); break;
            case 'n': sb.Append('\n'); break;
            case 'r': sb.Append('\r'); break;
            case 't': sb.Append('\t'); break;
            case 'u':
              int code;
              if (Pos + 4 > s.Length || !int.TryParse(s.Substring(Pos, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code))
                throw Err("bad \\u escape");
              sb.Append((char)code); Pos += 4; break;
            default: throw Err("bad escape");
          }
        }
      }

      double Num() {
        int start = Pos;
        if (s[Pos] == '-') Pos++;
        while (Pos < s.Length && "0123456789+-.eE".IndexOf(s[Pos]) >= 0) Pos++;
        double d;
        if (!double.TryParse(s.Substring(start, Pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
          throw Err("bad number");
        return d;
      }
    }
  }
}
