using AMMS.Application.Interfaces;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class TaskQrTokenService : ITaskQrTokenService
    {
        private readonly string _secret;
        public TaskQrTokenService(IConfiguration config)
        {
            _secret = config["Qr:Secret"] ?? throw new Exception("Missing Qr:Secret");
        }

        public string CreateToken(int taskId, int qtyGood, TimeSpan ttl)
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(ttl).ToUnixTimeSeconds();
            var nonce = Guid.NewGuid().ToString("N");
            var payload = $"{taskId}|{expiresAt}|{qtyGood}|{nonce}";
            var sig = Sign(payload);
            return $"{payload}|{sig}";
        }

        public bool TryValidate(string token, out int taskId, out int qtyGood, out string reason)
        {
            taskId = 0;
            reason = "";
            qtyGood = 0;

            var parts = token.Split('|');
            if (parts.Length != 5) { reason = "Invalid token format"; return false; }

            if (!int.TryParse(parts[0], out taskId)) { reason = "Invalid taskId"; return false; }
            if (!long.TryParse(parts[1], out var exp)) { reason = "Invalid expiry"; return false; }
            if (!int.TryParse(parts[2], out qtyGood)) { reason = "Invalid qty_good"; return false;}

            var payload = $"{parts[0]}|{parts[1]}|{parts[2]}|{parts[3]}";
            var sig = parts[4];

            if (!FixedEquals(sig, Sign(payload))) { reason = "Bad signature"; return false; }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > exp) 
            { 
                reason = "Token expired"; 
                return false; 
            }

            return true;
        }

        private string Sign(string payload)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            return Convert.ToHexString(hash);
        }

        private static bool FixedEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            return CryptographicOperations.FixedTimeEquals(ba, bb);
        }
    }
}
