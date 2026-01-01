using AMMS.Application.Interfaces;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Services
{
    public class ProcessCostRuleService : IProcessCostRuleService
    {
        private readonly IProcessCostRuleRepository _processCostRuleRepository;
        public ProcessCostRuleService(IProcessCostRuleRepository processCostRuleRepository)
        {
            _processCostRuleRepository = processCostRuleRepository;
        }
        public async Task<(decimal unitPrice, string unit, string note)> GetRateAsync(ProcessType p, CancellationToken ct = default)
        {
            return await _processCostRuleRepository.GetRateAsync(p, ct);
        }
    }
}
