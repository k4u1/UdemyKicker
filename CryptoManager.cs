using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace UdemyKicker
{
    public static class CryptoManager
    {
        public static string KeyForLocalFiles() => "khaledTaoludemyfucker78295ievwph";
        public static string KeyForEx() => "UdemyKicker_Static_File_Key_2026";

        public static byte[] GetSha256(string key)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(key));
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLower();
                return Encoding.UTF8.GetBytes(hex.Substring(0, 32));
            }
        }

        public static List<CourseItem> DecryptCommandFile(string content, string encryptionKey)
        {
            try
            {
                byte[] raw = Convert.FromBase64String(content);
                byte[] iv = new byte[16];
                byte[] ciphertext = new byte[raw.Length - 16];
                
                Array.Copy(raw, 0, iv, 0, 16);
                Array.Copy(raw, 16, ciphertext, 0, ciphertext.Length);

                byte[] key = GetSha256(encryptionKey);
                
                using (Aes aes = Aes.Create())
                {
                    aes.Key = key;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;

                    using (var decryptor = aes.CreateDecryptor(aes.Key, aes.IV))
                    using (var ms = new MemoryStream(ciphertext))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        string json = sr.ReadToEnd();
                        return JsonConvert.DeserializeObject<List<CourseItem>>(json);
                    }
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText("decrypt_error.txt", ex.ToString());
                try
                {
                    // Fallback legacy (unencrypted)
                    return JsonConvert.DeserializeObject<List<CourseItem>>(content);
                }
                catch
                {
                    return null;
                }
            }
        }

        public static string EncryptCommandFile(List<CourseItem> data, string encryptionKey)
        {
            string jsonStr = JsonConvert.DeserializeObject(JsonConvert.SerializeObject(data)).ToString();
            byte[] key = GetSha256(encryptionKey);
            byte[] iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(iv);
            }

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor(aes.Key, aes.IV))
                using (var ms = new MemoryStream())
                {
                    using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(jsonStr);
                    }
                    byte[] ciphertext = ms.ToArray();
                    byte[] result = new byte[iv.Length + ciphertext.Length];
                    Array.Copy(iv, 0, result, 0, iv.Length);
                    Array.Copy(ciphertext, 0, result, iv.Length, ciphertext.Length);
                    
                    return Convert.ToBase64String(result);
                }
            }
        }
    }
}
