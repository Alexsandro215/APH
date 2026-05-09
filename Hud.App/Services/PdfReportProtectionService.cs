using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;

namespace Hud.App.Services
{
    public static class PdfReportProtectionService
    {
        public static void ProtectIfEnabled(string pdfPath)
        {
            var settings = AppSettingsService.Load();
            if (!settings.ProtectReportsWithPassword || !ReportSecurityService.HasPassword(settings))
                return;

            var password = ReportSecurityService.TryGetReportPassword(settings);
            if (string.IsNullOrWhiteSpace(password))
                return;

            Protect(pdfPath, password);
        }

        private static void Protect(string pdfPath, string password)
        {
            using var document = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Modify);
            var security = document.SecuritySettings;
            security.UserPassword = password;
            security.OwnerPassword = $"{password}:aph-owner";
            security.PermitAnnotations = false;
            security.PermitAssembleDocument = false;
            security.PermitExtractContent = false;
            security.PermitFormsFill = false;
            security.PermitFullQualityPrint = false;
            security.PermitModifyDocument = false;
            security.PermitPrint = false;

            document.Save(pdfPath);
        }
    }
}
