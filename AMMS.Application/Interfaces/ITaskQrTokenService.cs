using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ITaskQrTokenService
    {
        string CreateToken(int taskId, int qtyGood, TimeSpan ttl);
        bool TryValidate(string token, out int taskId, out int qtyGood, out string reason);
    }
}
