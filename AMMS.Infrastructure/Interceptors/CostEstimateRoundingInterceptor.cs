using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using AMMS.Infrastructure.Entities;

namespace AMMS.Infrastructure.Interceptors
{
    public class CostEstimateRoundingInterceptor : SaveChangesInterceptor
    {
        private static decimal R(decimal value)
            => Math.Round(value, 0, MidpointRounding.AwayFromZero);

        private static void RoundCostEstimate(DbContext? context)
        {
            if (context == null) return;

            var entries = context.ChangeTracker.Entries<cost_estimate>()
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

            foreach (var e in entries)
            {
                var x = e.Entity;

                // ===== COST =====
                x.paper_cost = R(x.paper_cost);
                x.paper_unit_price = R(x.paper_unit_price);

                x.ink_cost = R(x.ink_cost);
                x.coating_glue_cost = R(x.coating_glue_cost);
                x.mounting_glue_cost = R(x.mounting_glue_cost);
                x.lamination_cost = R(x.lamination_cost);

                x.material_cost = R(x.material_cost);
                x.overhead_cost = R(x.overhead_cost);
                x.base_cost = R(x.base_cost);

                x.rush_amount = R(x.rush_amount);
                x.subtotal = R(x.subtotal);
                x.discount_amount = R(x.discount_amount);

                x.final_total_cost = R(x.final_total_cost);
            }
        }

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData,
            InterceptionResult<int> result)
        {
            RoundCostEstimate(eventData.Context);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            RoundCostEstimate(eventData.Context);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
