using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IDealService
    {
        Task SendDealAndEmailAsync(int orderRequestId);
        Task RejectDealAsync(int orderRequestId, string reason);
        Task AcceptDealAsync(int orderRequestId);
    }
}
