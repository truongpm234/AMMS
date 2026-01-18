using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface ISmsSender
    {
        Task SendAsync(string phone, string message, CancellationToken ct = default);
    }
}
