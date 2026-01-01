using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Interfaces;
using AMMS.Shared.DTOs.Enums;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Repositories
{
    public class ProcessCostRuleRepository : IProcessCostRuleRepository
    {
        private readonly AppDbContext _db;

        public ProcessCostRuleRepository(AppDbContext db)
        {
            _db = db;
        }

        public async Task<(decimal unitPrice, string unit, string note)> GetRateAsync(ProcessType p, CancellationToken ct = default)
        {
            var code = p.ToString();

            var rule = await _db.process_cost_rules
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.process_code == code, ct);

            if (rule == null)
            {
                return (0m, "", "Chưa cấu hình đơn giá trong process_cost_rule");
            }

            return (rule.unit_price, rule.unit, rule.note ?? "");
        }
    }
}
