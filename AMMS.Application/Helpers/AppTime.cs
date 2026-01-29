using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Helpers
{
    public static class AppTime
    {
        private static readonly TimeZoneInfo VnTz =
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        public static DateTime NowVn()
            => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, VnTz);

        public static DateTime NowVnUnspecified()
            => DateTime.SpecifyKind(NowVn(), DateTimeKind.Unspecified);
    }
}
