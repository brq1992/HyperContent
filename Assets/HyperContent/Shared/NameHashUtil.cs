using System.Security.Cryptography;
using System.Text;

namespace HyperContent.Shared
{
    /// <summary>
    /// Stable hash for asset names. Used for NameAlias lookup (GUID vs Name).
    /// Same input always produces same 64-char hex string across runs.
    /// </summary>
    public static class NameHashUtil
    {
        /// <summary>
        /// Compute stable SHA256 hash of name as 64-char lowercase hex string.
        /// </summary>
        public static string Compute(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(name);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(bytes);
                return BytesToHex(hash);
            }
        }

        private static string BytesToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
