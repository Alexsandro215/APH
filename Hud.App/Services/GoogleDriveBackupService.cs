using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using Google.Apis.Util.Store;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace Hud.App.Services
{
    public sealed record GoogleDriveBackupResult(bool Success, string Message, DateTimeOffset? SyncedAt = null, string? AccountEmail = null);

    public static class GoogleDriveBackupService
    {
        private const string ApplicationName = "APH";
        private const string BackupFileName = "aph.db";
        private const string BackupMimeType = "application/x-sqlite3";

        private static readonly string[] Scopes =
        {
            "openid",
            "https://www.googleapis.com/auth/userinfo.email",
            "https://www.googleapis.com/auth/userinfo.profile",
            DriveService.Scope.DriveAppdata
        };

        public static string CredentialsFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "APH");

        public static string PrimaryCredentialsPath =>
            Path.Combine(CredentialsFolder, "google_client_secret.json");

        private static string TokenFolder =>
            Path.Combine(CredentialsFolder, "GoogleDriveToken");

        public static bool HasCredentialsFile =>
            ResolveCredentialsPath() is not null;

        public static bool HasStoredToken =>
            Directory.Exists(TokenFolder) &&
            Directory.EnumerateFiles(TokenFolder, "*", SearchOption.AllDirectories).Any();

        public static void ClearStoredToken()
        {
            try
            {
                if (Directory.Exists(TokenFolder))
                    Directory.Delete(TokenFolder, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"APH Google token cleanup failed: {ex.Message}");
            }
        }

        public static async Task<GoogleDriveBackupResult> ConnectAsync(CancellationToken cancellationToken = default)
        {
            var session = await CreateDriveSessionAsync(cancellationToken);
            return new GoogleDriveBackupResult(
                true,
                string.IsNullOrWhiteSpace(session.AccountEmail)
                    ? $"Google Drive conectado. Respaldo oculto en appDataFolder: {BackupFileName}"
                    : $"Google Drive conectado como {session.AccountEmail}. Respaldo oculto: {BackupFileName}",
                DateTimeOffset.Now,
                session.AccountEmail);
        }

        public static async Task<GoogleDriveBackupResult> UploadDatabaseAsync(CancellationToken cancellationToken = default)
        {
            if (!File.Exists(AphBackupDatabaseService.DatabasePath))
                return new GoogleDriveBackupResult(false, "Aun no existe aph.db. Carga o analiza manos primero para crear el respaldo local.");

            var session = await CreateDriveSessionAsync(cancellationToken);
            var service = session.Service;
            var snapshot = CreateDatabaseSnapshot();

            try
            {
                var existing = await FindBackupFileAsync(service, cancellationToken);
                await using var stream = File.OpenRead(snapshot);

                IUploadProgress uploadProgress;
                if (existing is null)
                {
                    var metadata = new DriveFile
                    {
                        Name = BackupFileName,
                        Parents = new[] { "appDataFolder" }
                    };
                    var create = service.Files.Create(metadata, stream, BackupMimeType);
                    create.Fields = "id,name,modifiedTime";
                    uploadProgress = await create.UploadAsync(cancellationToken);
                }
                else
                {
                    var metadata = new DriveFile
                    {
                        Name = BackupFileName
                    };
                    var update = service.Files.Update(metadata, existing.Id, stream, BackupMimeType);
                    update.Fields = "id,name,modifiedTime";
                    uploadProgress = await update.UploadAsync(cancellationToken);
                }

                if (uploadProgress.Status == UploadStatus.Failed)
                    return new GoogleDriveBackupResult(false, $"No se pudo subir a Drive: {uploadProgress.Exception?.Message}");

                return new GoogleDriveBackupResult(true, "Respaldo aph.db subido a Google Drive.", DateTimeOffset.Now, session.AccountEmail);
            }
            finally
            {
                TryDelete(snapshot);
            }
        }

        public static async Task<GoogleDriveBackupResult> RestoreDatabaseAsync(CancellationToken cancellationToken = default)
        {
            var session = await CreateDriveSessionAsync(cancellationToken);
            var service = session.Service;
            var existing = await FindBackupFileAsync(service, cancellationToken);
            if (existing is null)
                return new GoogleDriveBackupResult(false, "No encontre aph.db en el Drive de APH.", AccountEmail: session.AccountEmail);

            Directory.CreateDirectory(Path.GetDirectoryName(AphBackupDatabaseService.DatabasePath)!);
            var tempPath = Path.Combine(Path.GetTempPath(), $"aph_drive_restore_{Guid.NewGuid():N}.db");

            try
            {
                await using (var stream = File.Create(tempPath))
                {
                    var download = await service.Files.Get(existing.Id).DownloadAsync(stream, cancellationToken);
                    if (download.Status != Google.Apis.Download.DownloadStatus.Completed)
                        return new GoogleDriveBackupResult(false, $"No se pudo descargar de Drive: {download.Exception?.Message}");
                }

                if (File.Exists(AphBackupDatabaseService.DatabasePath))
                {
                    var backupPath = Path.Combine(
                        Path.GetDirectoryName(AphBackupDatabaseService.DatabasePath)!,
                        $"aph.before_drive_restore.{DateTime.Now:yyyyMMdd_HHmmss}.bak");
                    File.Copy(AphBackupDatabaseService.DatabasePath, backupPath, overwrite: true);
                }

                File.Copy(tempPath, AphBackupDatabaseService.DatabasePath, overwrite: true);
                return new GoogleDriveBackupResult(true, "Base aph.db restaurada desde Google Drive. Reinicia APH para recargar todo limpio.", DateTimeOffset.Now, session.AccountEmail);
            }
            finally
            {
                TryDelete(tempPath);
            }
        }

        private static async Task<GoogleDriveSession> CreateDriveSessionAsync(CancellationToken cancellationToken)
        {
            var credentialsPath = ResolveCredentialsPath();
            if (credentialsPath is null)
            {
                Directory.CreateDirectory(CredentialsFolder);
                throw new FileNotFoundException($"Falta google_client_secret.json en {CredentialsFolder}");
            }

            await using var stream = File.OpenRead(credentialsPath);
            var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
            var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                Scopes,
                "aph-user",
                cancellationToken,
                new FileDataStore(TokenFolder, true));

            var accountEmail = await GetAccountEmailAsync(credential, cancellationToken);

            var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName
            });

            return new GoogleDriveSession(service, accountEmail);
        }

        private static async Task<string?> GetAccountEmailAsync(UserCredential credential, CancellationToken cancellationToken)
        {
            try
            {
                var token = await credential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await client.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                return document.RootElement.TryGetProperty("email", out var email)
                    ? email.GetString()
                    : null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"APH Google account email lookup failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<DriveFile?> FindBackupFileAsync(DriveService service, CancellationToken cancellationToken)
        {
            var list = service.Files.List();
            list.Spaces = "appDataFolder";
            list.Q = $"name = '{BackupFileName}' and trashed = false";
            list.Fields = "files(id,name,modifiedTime,size)";
            list.PageSize = 1;

            var result = await list.ExecuteAsync(cancellationToken);
            return result.Files?.FirstOrDefault();
        }

        private static string CreateDatabaseSnapshot()
        {
            var snapshot = Path.Combine(Path.GetTempPath(), $"aph_drive_snapshot_{Guid.NewGuid():N}.db");
            File.Copy(AphBackupDatabaseService.DatabasePath, snapshot, overwrite: true);
            return snapshot;
        }

        private static string? ResolveCredentialsPath()
        {
            var candidates = new[]
            {
                PrimaryCredentialsPath,
                Path.Combine(AppContext.BaseDirectory, "google_client_secret.json"),
                Path.Combine(AppContext.BaseDirectory, "client_secret.json")
            };

            return candidates.FirstOrDefault(File.Exists);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        private sealed record GoogleDriveSession(DriveService Service, string? AccountEmail);
    }
}
