using System.Security.Cryptography;
using System.Text;

namespace Hud.App.Services
{
    public static class ReportSecurityService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 180_000;

        public static bool HasPassword(AppSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.ReportPasswordSalt) &&
            !string.IsNullOrWhiteSpace(settings.ReportPasswordHash);

        public static void SetPassword(AppSettings settings, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Hash(password, salt);
            settings.ReportPasswordSalt = Convert.ToBase64String(salt);
            settings.ReportPasswordHash = Convert.ToBase64String(hash);
            settings.EncryptedReportPassword = ProtectPassword(password);
            settings.ProtectReportsWithPassword = true;
        }

        public static string? TryGetReportPassword(AppSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.EncryptedReportPassword))
                return null;

            try
            {
                var protectedBytes = Convert.FromBase64String(settings.EncryptedReportPassword);
                var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }

        public static bool VerifyPassword(AppSettings settings, string password)
        {
            if (!HasPassword(settings))
                return false;

            try
            {
                var salt = Convert.FromBase64String(settings.ReportPasswordSalt!);
                var expected = Convert.FromBase64String(settings.ReportPasswordHash!);
                var actual = Hash(password, salt);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
        }

        public static void ClearPassword(AppSettings settings)
        {
            settings.ReportPasswordSalt = null;
            settings.ReportPasswordHash = null;
            settings.EncryptedReportPassword = null;
            settings.ProtectReportsWithPassword = false;
        }

        private static string ProtectPassword(string password)
        {
            var bytes = Encoding.UTF8.GetBytes(password);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        private static byte[] Hash(string password, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                HashSize);
    }
}
