using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Forge.Providers
{
    /// <summary>
    /// DPAPI-protected API-key store (System.Security.Cryptography.ProtectedData, CurrentUser scope).
    /// Keys are kept as Base64 of the protected blob at %APPDATA%\Forge\keys\{provider}.key.
    /// Key material is never logged; Load returns null instead of throwing.
    /// </summary>
    public static class KeyStore
    {
        private static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Forge", "keys");
            }
        }

        private static string PathFor(string provider)
        {
            return Path.Combine(Dir, Sanitize(provider) + ".key");
        }

        private static string Sanitize(string provider)
        {
            if (string.IsNullOrWhiteSpace(provider)) return "default";
            var sb = new StringBuilder();
            foreach (char c in provider)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            string s = sb.ToString().Trim('_');
            return s.Length == 0 ? "default" : s;
        }

        /// <summary>Protect + persist a key. Throws if the key cannot be saved (fail closed).</summary>
        public static void Save(string provider, string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("apiKey must not be empty.", "apiKey");
            byte[] plain = Encoding.UTF8.GetBytes(apiKey);
            byte[] protectedBlob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Dir);
            File.WriteAllText(PathFor(provider), Convert.ToBase64String(protectedBlob));
        }

        /// <summary>Returns the plaintext key, or null if missing/unreadable (caller falls back).</summary>
        public static string Load(string provider)
        {
            string path = PathFor(provider);
            if (!File.Exists(path)) return null;
            try
            {
                string b64 = File.ReadAllText(path).Trim();
                byte[] protectedBlob = Convert.FromBase64String(b64);
                byte[] plain = ProtectedData.Unprotect(protectedBlob, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Removes the stored key. Returns true if a file was deleted.</summary>
        public static bool Delete(string provider)
        {
            string path = PathFor(provider);
            if (!File.Exists(path)) return false;
            try { File.Delete(path); return true; }
            catch { return false; }
        }
    }
}
