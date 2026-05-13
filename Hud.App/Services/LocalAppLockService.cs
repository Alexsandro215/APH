using System.Security.Cryptography;

namespace Hud.App.Services
{
    public static class LocalAppLockService
    {
        private const int SaltSize = 16;
        private const int HashSize = 32;
        private const int Iterations = 180_000;

        public static bool HasPassword(AppSettings settings) =>
            !string.IsNullOrWhiteSpace(settings.LocalAppPasswordSalt) &&
            !string.IsNullOrWhiteSpace(settings.LocalAppPasswordHash);

        public static void SetPassword(AppSettings settings, string password)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var hash = Hash(password, salt);
            settings.LocalAppPasswordSalt = Convert.ToBase64String(salt);
            settings.LocalAppPasswordHash = Convert.ToBase64String(hash);
        }

        public static void ClearPassword(AppSettings settings)
        {
            settings.LocalAppPasswordSalt = null;
            settings.LocalAppPasswordHash = null;
        }

        public static bool VerifyPassword(AppSettings settings, string password)
        {
            if (!HasPassword(settings))
                return false;

            try
            {
                var salt = Convert.FromBase64String(settings.LocalAppPasswordSalt!);
                var expected = Convert.FromBase64String(settings.LocalAppPasswordHash!);
                var actual = Hash(password, salt);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch
            {
                return false;
            }
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
