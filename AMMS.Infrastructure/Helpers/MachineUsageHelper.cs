using AMMS.Infrastructure.DBContext;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Infrastructure.Helpers
{
    public static class MachineUsageHelper
    {
        // Reserve qty machines for a machine_code
        public static async Task ReserveAsync(AppDbContext db, string? machineCode, int qty, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(machineCode)) return;
            if (qty <= 0) return;

            var mc = await db.machines.FirstOrDefaultAsync(x => x.machine_code == machineCode, ct);
            if (mc == null) return;

            var total = mc.quantity ;
            var busy = mc.busy_quantity ?? 0;

            // if free null -> compute
            var free = mc.free_quantity ?? Math.Max(0, total - busy);

            // cannot reserve more than free
            var alloc = Math.Min(qty, free);

            busy += alloc;
            free = Math.Max(0, total - busy);

            mc.busy_quantity = busy;
            mc.free_quantity = free;
        }

        // Release qty machines for a machine_code
        public static async Task ReleaseAsync(AppDbContext db, string? machineCode, int qty, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(machineCode)) return;
            if (qty <= 0) return;

            var mc = await db.machines.FirstOrDefaultAsync(x => x.machine_code == machineCode, ct);
            if (mc == null) return;

            var total = mc.quantity;
            var busy = mc.busy_quantity ?? 0;

            busy = Math.Max(0, busy - qty);
            var free = Math.Max(0, total - busy);

            mc.busy_quantity = busy;
            mc.free_quantity = free;
        }

        // Reserve ALL machines (machines.quantity) for that machine_code
        public static async Task ReserveAllAsync(AppDbContext db, string? machineCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(machineCode)) return;

            var mc = await db.machines.FirstOrDefaultAsync(x => x.machine_code == machineCode, ct);
            if (mc == null) return;

            var total = mc.quantity;
            await ReserveAsync(db, machineCode, total, ct);
        }

        // Release ALL machines (machines.quantity) for that machine_code
        public static async Task ReleaseAllAsync(AppDbContext db, string? machineCode, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(machineCode)) return;

            var mc = await db.machines.FirstOrDefaultAsync(x => x.machine_code == machineCode, ct);
            if (mc == null) return;

            var total = mc.quantity;
            await ReleaseAsync(db, machineCode, total, ct);
        }
    }
}
