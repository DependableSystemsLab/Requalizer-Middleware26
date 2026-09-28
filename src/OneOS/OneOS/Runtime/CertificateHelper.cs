using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OneOS.Runtime
{
    public static class CertificateHelper
    {
        public static void GenerateSelfSignedCertificate(string pfxPath, string subjectName, string password = "")
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection 
                { 
                    new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                    new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
                }, false));

            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = DateTimeOffset.UtcNow.AddYears(10);

            using var cert = request.CreateSelfSigned(notBefore, notAfter);
            var pfxBytes = cert.Export(X509ContentType.Pfx, password);
            File.WriteAllBytes(pfxPath, pfxBytes);
        }

        public static void GenerateCertificateSigningRequest(string csrPath, string privateKeyPath, string subjectName)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            string csrPem = request.CreateSigningRequestPem();
            File.WriteAllText(csrPath, csrPem);

            string privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();
            File.WriteAllText(privateKeyPath, privateKeyPem);
        }

        public static string GetCertHashFromFile(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            
            using X509Certificate2 cert = (ext == ".pfx" || ext == ".p12") 
                ? X509CertificateLoader.LoadPkcs12FromFile(filePath, "") 
                : X509CertificateLoader.LoadCertificateFromFile(filePath);
                
            return cert.GetCertHashString();
        }

        public static async System.Threading.Tasks.Task<string> GetCertHashFromRemoteAsync(string host, int port)
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(host, port).ConfigureAwait(false);

            string hash = string.Empty;

            using var sslStream = new System.Net.Security.SslStream(client.GetStream(), false, (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate != null)
                {
                    hash = certificate.GetCertHashString();
                }
                // Always return false to intentionally abort the handshake after we've seen the cert.
                // We only connected to grab the hash.
                return false;
            });

            try
            {
                await sslStream.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                {
                    TargetHost = host
                }).ConfigureAwait(false);
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                // Expected, since we returned false from the validation callback
            }
            catch (IOException)
            {
                // Also can be thrown when handshake aborts
            }

            if (string.IsNullOrEmpty(hash))
            {
                throw new Exception("Failed to retrieve certificate from the remote endpoint.");
            }

            return hash;
        }
    }
}
