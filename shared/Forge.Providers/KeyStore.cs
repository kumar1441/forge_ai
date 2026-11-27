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
                throw new ArgumentException("Enter a key before saving.", "apiKey");
            byte[] protectedBlob;
            try
            {
                byte[] plain = Encoding.UTF8.GetBytes(apiKey);
                try { protectedBlob = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (CryptographicException ex)
            {
                string detail = null;
                try { detail = ex.Message; if (detail != null && detail.Length > 200) detail = detail.Substring(0, 200) + "…"; } catch { detail = null; }
                throw new CryptographicException("Could not lock the key in Windows secure storage — use a local Windows account profile (roaming profiles can block it). (detail: " + (detail ?? "secure-storage error") + ")", ex);
            }
            Directory.CreateDirectory(Dir);
            // Atomic write (temp + replace): a crash or a concurrent Save/Load can
            // never leave a half-written .key file behind that Load then misreads.
            string path = PathFor(provider);
            string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, Convert.ToBase64String(protectedBlob));
                // Replace-or-move with fallback both ways: the Exists check can race a
                // concurrent rotation/removal between check and write.
                bool replaced = false;
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, null); replaced = true; }
                    catch (FileNotFoundException) { replaced = false; }
                }
                if (!replaced)
                {
                    try { File.Move(tmp, path); }
                    catch (IOException) { File.Replace(tmp, path, null); }
                }
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
            // Read-back verification: a save this user context cannot decrypt
            // (roaming profile) must fail closed here, not surface as hasKey=false later.
            string check = null;
            try { check = Load(provider); } catch { check = null; }
            if (!string.Equals(check, apiKey, StringComparison.Ordinal))
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(tmp); } catch { }
                throw new CryptographicException("The key was saved but could not be read back in Windows secure storage — use a local Windows account profile (roaming profiles can block it).");
            }
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
                try
                {
                    string s = Encoding.UTF8.GetString(plain);
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                finally { Array.Clear(plain, 0, plain.Length); }
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
