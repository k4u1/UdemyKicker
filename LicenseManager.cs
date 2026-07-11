using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.Net.Http.Headers;

namespace UdemyKicker
{
    public class LicenseManager
    {
        private const string REG_PATH = @"Software\UdemyKicker";

        public string GetHWID()
        {
            try
            {
                string uuid = RunCommand("wmic", "csproduct get uuid").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[1].Trim();
                string cpuId = RunCommand("wmic", "cpu get processorid").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)[1].Trim();
                
                string combined = $"{uuid}-{cpuId}";
                using (var sha256 = SHA256.Create())
                {
                    byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(combined));
                    return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToUpper();
                }
            }
            catch
            {
                return "UNKNOWN-DEVICE-ID";
            }
        }

        private string RunCommand(string filename, string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = filename,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }

        public async Task<DateTime?> GetNetworkTimeAsync()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetAsync("https://worldtimeapi.org/api/timezone/UTC");
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        dynamic data = JsonConvert.DeserializeObject(json);
                        string dtStr = data.datetime;
                        return DateTime.Parse(dtStr.Substring(0, 16));
                    }
                }
            }
            catch { }

            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = await client.GetAsync("https://google.com");
                    if (response.Headers.Date.HasValue)
                    {
                        return response.Headers.Date.Value.UtcDateTime;
                    }
                }
            }
            catch { }

            return null;
        }

        public string GetSavedKey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_PATH))
                {
                    if (key != null)
                    {
                        return key.GetValue("LicenseKey") as string;
                    }
                }
            }
            catch { }
            return null;
        }

        public bool SaveKey(string key)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.CreateSubKey(REG_PATH))
                {
                    rk.SetValue("LicenseKey", key);
                    return true;
                }
            }
            catch { return false; }
        }

        public string GenerateFernetKey(string hid)
        {
            string salt = "UdemyKicker_Secure_2026_Fixed_Key";
            byte[] hidBytes = System.Text.Encoding.UTF8.GetBytes(hid);
            byte[] saltBytes = System.Text.Encoding.UTF8.GetBytes(salt);
            byte[] combined = new byte[hidBytes.Length + saltBytes.Length];
            Buffer.BlockCopy(hidBytes, 0, combined, 0, hidBytes.Length);
            Buffer.BlockCopy(saltBytes, 0, combined, hidBytes.Length, saltBytes.Length);

            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(combined);
                string base64 = Convert.ToBase64String(hash);
                return base64.Replace('+', '-').Replace('/', '_');
            }
        }

        public async Task<(bool isValid, string message, DateTime expiryDate)> ValidateKeyAsync(string encKey, DateTime localNetworkTime)
        {
            string clientHwid = GetHWID();
            try
            {
                string fernetKey = GenerateFernetKey(clientHwid);
                string decryptedJson = FernetDecryptor.Decrypt(encKey, fernetKey);
                
                dynamic payload = JsonConvert.DeserializeObject(decryptedJson);
                string payloadHwid = payload.hid;
                string expiryStr = payload.expiry;

                if (string.Equals(payloadHwid, clientHwid, StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.TryParse(expiryStr, out DateTime expiryDate))
                    {
                        if (localNetworkTime < expiryDate)
                        {
                            return (true, "Valid", expiryDate);
                        }
                        else
                        {
                            return (false, "Expired", expiryDate);
                        }
                    }
                    else
                    {
                        return (false, "Invalid Expiry Format", DateTime.MinValue);
                    }
                }
                else
                {
                    return (false, "HWID Mismatch", DateTime.MinValue);
                }
            }
            catch (Exception ex)
            {
                return (false, "Decryption Failed: " + ex.Message, DateTime.MinValue);
            }
        }
    }

    public static class FernetDecryptor
    {
        public static string Decrypt(string tokenStr, string keyStr)
        {
            byte[] keyBytes = DecodeUrlSafeBase64(keyStr);
            if (keyBytes.Length != 32)
                throw new ArgumentException("Fernet key must be 32 bytes.");

            byte[] signingKey = new byte[16];
            byte[] encryptionKey = new byte[16];
            Buffer.BlockCopy(keyBytes, 0, signingKey, 0, 16);
            Buffer.BlockCopy(keyBytes, 16, encryptionKey, 0, 16);

            byte[] tokenBytes = DecodeUrlSafeBase64(tokenStr);
            if (tokenBytes.Length < 57)
                throw new FormatException("Token is too short.");

            if (tokenBytes[0] != 0x80)
                throw new FormatException("Invalid Fernet version.");

            int hmacOffset = tokenBytes.Length - 32;
            byte[] receivedHmac = new byte[32];
            Buffer.BlockCopy(tokenBytes, hmacOffset, receivedHmac, 0, 32);

            using (var hmac = new HMACSHA256(signingKey))
            {
                byte[] calculatedHmac = hmac.ComputeHash(tokenBytes, 0, hmacOffset);
                if (!CryptographicEquals(calculatedHmac, receivedHmac))
                    throw new CryptographicException("HMAC signature verification failed.");
            }

            byte[] iv = new byte[16];
            Buffer.BlockCopy(tokenBytes, 9, iv, 0, 16);

            int ciphertextLen = tokenBytes.Length - 57;
            byte[] ciphertext = new byte[ciphertextLen];
            Buffer.BlockCopy(tokenBytes, 25, ciphertext, 0, ciphertextLen);

            using (var aes = Aes.Create())
            {
                aes.KeySize = 128;
                aes.Key = encryptionKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new MemoryStream(ciphertext))
                using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (var reader = new StreamReader(cs, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static byte[] DecodeUrlSafeBase64(string s)
        {
            string incoming = s.Replace('_', '/').Replace('-', '+');
            switch (incoming.Length % 4)
            {
                case 2: incoming += "=="; break;
                case 3: incoming += "="; break;
            }
            return Convert.FromBase64String(incoming);
        }

        private static bool CryptographicEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int result = 0;
            for (int i = 0; i < a.Length; i++)
            {
                result |= a[i] ^ b[i];
            }
            return result == 0;
        }
    }
}
