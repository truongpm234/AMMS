using AMMS.Shared.DTOs.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Interfaces
{
    public interface IProcessCostRuleService
    {
        Task<(decimal unitPrice, string unit, string note)> GetRateAsync(ProcessType p, CancellationToken ct = default);
    }

}
