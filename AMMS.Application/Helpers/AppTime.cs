using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Helpers
{
    public static class AppTime
    {
        public static readonly TimeZoneInfo VietNamTz =
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "SE Asia Standard Time" : "Asia/Ho_Chi_Minh"
            );

        public static DateTime UtcNowUnspecified()
            => DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        public static DateTime ToVnTime(DateTime utcUnspecified)
        {
            var utc = DateTime.SpecifyKind(utcUnspecified, DateTimeKind.Utc);
            var vn = TimeZoneInfo.ConvertTimeFromUtc(utc, VietNamTz);
            return DateTime.SpecifyKind(vn, DateTimeKind.Unspecified);
        }
    }

}
