using AMMS.Infrastructure.DBContext;
using AMMS.Infrastructure.Entities;
using AMMS.Shared.Helpers;
using Microsoft.EntityFrameworkCore;

namespace AMMS.Application.Helpers
{
    public sealed class MachineTaskPlanDto
    {
        public int process_id { get; set; }

        public int seq_num { get; set; }

        public string process_code { get; set; } = "";

        public string process_name { get; set; } = "";

        public string machine_code { get; set; } = "";

        public DateTime planned_start_time { get; set; }

        public DateTime planned_end_time { get; set; }

        public decimal required_units { get; set; }

        public decimal effective_capacity_per_hour { get; set; }
    }

    public sealed class MachineReservationDto
    {
        public string machine_code { get; set; } = "";

        public DateTime start { get; set; }

        public DateTime end { get; set; }
    }

    public static class ProductionMachinePlanHelper
    {
        public static async Task<DateTime> ResolveEarliestScheduleAnchorAsync(
            AppDbContext db,
            WorkCalendar cal,
            CancellationToken ct)
        {
            var now = AppTime.NowVnUnspecified();

            var hours = await db.estimate_config
                .AsNoTracking()
                .Where(x =>
                    x.config_group == "planning" &&
                    x.config_key == "min_start_wait_hours")
                .OrderByDescending(x => x.updated_at)
                .Select(x => (decimal?)x.value_num)
                .FirstOrDefaultAsync(ct);

            var resolvedHours = hours.HasValue && hours.Value > 0m
                ? hours.Value
                : 24m;

            var start = now.AddMinutes((double)(resolvedHours * 60m));

            /*
             * Không cho planned_start rơi vào cùng ngày xác nhận lập lịch.
             */
            if (start.Date <= now.Date)
                start = now.Date.AddDays(1);

            return cal.NormalizeStart(start);
        }

        public static async Task<List<MachineTaskPlanDto>> BuildMachinePlanAsync(
            AppDbContext db,
            WorkCalendar cal,
            int productTypeId,
            IReadOnlyList<string> processCodes,
            decimal quantity,
            DateTime earliestStart,
            List<MachineReservationDto>? inMemoryReservations,
            Dictionary<string, decimal>? requiredUnitsByProcessCode,
            CancellationToken ct)
        {
            if (productTypeId <= 0)
                throw new InvalidOperationException("product_type_id không hợp lệ.");

            var codes = NormalizeProcessCodes(processCodes);

            if (codes.Count == 0)
                throw new InvalidOperationException("Danh sách công đoạn lập lịch bị rỗng.");

            quantity = quantity <= 0 ? 1 : quantity;

            inMemoryReservations ??= new List<MachineReservationDto>();
            requiredUnitsByProcessCode ??= new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

            var allSteps = await db.product_type_processes
                .AsNoTracking()
                .Where(x =>
                    x.product_type_id == productTypeId &&
                    (x.is_active ?? true))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.process_id)
                .ToListAsync(ct);

            var steps = allSteps
                .Where(x => codes.Contains(Norm(x.process_code)))
                .OrderBy(x => x.seq_num)
                .ThenBy(x => x.process_id)
                .ToList();

            if (steps.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Không tìm thấy product_type_process cho product_type_id={productTypeId}, process={string.Join(",", codes)}.");
            }

            var missing = codes
                .Where(code => !steps.Any(s => SameCode(s.process_code, code)))
                .ToList();

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Thiếu cấu hình product_type_process cho công đoạn: {string.Join(",", missing)}.");
            }

            var result = new List<MachineTaskPlanDto>();

            var cursor = cal.NormalizeStart(earliestStart);

            foreach (var step in steps)
            {
                var processCode = Norm(step.process_code);

                var machine = await ResolveMachineAsync(
                    db,
                    step,
                    processCode,
                    ct);

                var requiredUnits = ResolveRequiredUnits(
                    processCode,
                    quantity,
                    requiredUnitsByProcessCode);

                var effectiveCapacity = ResolveEffectiveCapacityPerHour(machine);

                var durationHours = ResolveDurationHours(
                    processCode,
                    requiredUnits,
                    effectiveCapacity);

                var busy = await LoadBusyIntervalsAsync(
                    db,
                    machine.machine_code,
                    cursor,
                    ct);

                busy.AddRange(
                    inMemoryReservations
                        .Where(x => SameCode(x.machine_code, machine.machine_code))
                        .Select(x => new BusyInterval
                        {
                            Start = x.start,
                            End = x.end
                        }));

                busy = busy
                    .Where(x => x.End > cursor)
                    .OrderBy(x => x.Start)
                    .ToList();

                var start = FindEarliestMachineSlot(
                    cal,
                    machine,
                    processCode,
                    cursor,
                    durationHours,
                    busy);

                var end = cal.AddWorkingHours(start, durationHours);

                var plan = new MachineTaskPlanDto
                {
                    process_id = step.process_id,
                    seq_num = step.seq_num,
                    process_code = processCode,
                    process_name = step.process_name ?? processCode,
                    machine_code = machine.machine_code,
                    planned_start_time = start,
                    planned_end_time = end,
                    required_units = requiredUnits,
                    effective_capacity_per_hour = effectiveCapacity
                };

                result.Add(plan);

                /*
                 * Reservation tạm để preview/create group không tự đụng chính nó.
                 */
                inMemoryReservations.Add(new MachineReservationDto
                {
                    machine_code = machine.machine_code,
                    start = start,
                    end = end
                });

                /*
                 * Công đoạn trong cùng production/order phải tuần tự.
                 */
                cursor = cal.NormalizeStart(end.AddMinutes(GetHandoffMinutes(processCode)));
            }

            return result;
        }

        private static async Task<machine> ResolveMachineAsync(
            AppDbContext db,
            product_type_process step,
            string processCode,
            CancellationToken ct)
        {
            var wantedMachineCode = !string.IsNullOrWhiteSpace(step.machine)
                ? step.machine.Trim()
                : processCode;

            var machines = await db.machines
                .AsNoTracking()
                .Where(x => x.is_active)
                .ToListAsync(ct);

            var exact = machines
                .Where(x => SameCode(x.machine_code, wantedMachineCode))
                .OrderByDescending(x => x.capacity_per_hour)
                .ThenByDescending(x => x.quantity)
                .ThenBy(x => x.machine_id)
                .FirstOrDefault();

            if (exact != null)
                return exact;

            var byProcess = machines
                .Where(x => SameCode(x.process_code, processCode))
                .OrderByDescending(x => x.capacity_per_hour)
                .ThenByDescending(x => x.quantity)
                .ThenBy(x => x.machine_id)
                .FirstOrDefault();

            if (byProcess != null)
                return byProcess;

            throw new InvalidOperationException(
                $"Không tìm thấy máy active cho công đoạn {processCode}.");
        }

        private static async Task<List<BusyInterval>> LoadBusyIntervalsAsync(
            AppDbContext db,
            string machineCode,
            DateTime anchor,
            CancellationToken ct)
        {
            var key = Norm(machineCode);

            var rows = await (
                from t in db.tasks.AsNoTracking()

                join p0 in db.productions.AsNoTracking()
                    on t.prod_id equals p0.prod_id into pj
                from p in pj.DefaultIfEmpty()

                where t.machine != null
                      && t.machine.Trim().ToUpper() == key
                      && !(
                            t.status != null &&
                            t.status.ToUpper() == "FINISHED"
                         )
                      && (
                            p == null ||
                            p.status == null ||
                            (
                                p.status.ToUpper() != "CANCELLED" &&
                                p.status.ToUpper() != "COMPLETED" &&
                                p.status.ToUpper() != "FINISHED" &&
                                p.status.ToUpper() != "IMPORTING" &&
                                p.status.ToUpper() != "DELIVERY"
                            )
                         )

                select new
                {
                    /*
                     * Quan trọng:
                     * Ưu tiên actual trước.
                     */
                    Start = t.start_time ?? t.planned_start_time,
                    End = t.end_time ?? t.planned_end_time
                }
            ).ToListAsync(ct);

            return rows
                .Where(x => x.Start.HasValue && x.End.HasValue)
                .Select(x => new BusyInterval
                {
                    Start = DateTime.SpecifyKind(x.Start!.Value, DateTimeKind.Unspecified),
                    End = DateTime.SpecifyKind(x.End!.Value, DateTimeKind.Unspecified)
                })
                .Where(x => x.End > anchor)
                .OrderBy(x => x.Start)
                .ToList();
        }

        private static DateTime FindEarliestMachineSlot(
            WorkCalendar cal,
            machine machine,
            string processCode,
            DateTime earliestStart,
            double durationHours,
            List<BusyInterval> intervals)
        {
            var capacity = Math.Max(1, machine.quantity);
            var cursor = cal.NormalizeStart(earliestStart);

            for (var guard = 0; guard < 5000; guard++)
            {
                var plannedEnd = cal.AddWorkingHours(cursor, durationHours);

                var overlapping = intervals
                    .Where(x => x.Start < plannedEnd && x.End > cursor)
                    .OrderBy(x => x.End)
                    .ToList();

                if (overlapping.Count < capacity)
                    return cursor;

                var nextCursor = overlapping.Min(x => x.End)
                    .AddMinutes(GetTurnaroundMinutes(processCode));

                cursor = cal.NormalizeStart(nextCursor);
            }

            throw new InvalidOperationException(
                $"Không tìm được slot trống cho máy {machine.machine_code}, process={processCode}.");
        }

        private static decimal ResolveEffectiveCapacityPerHour(machine machine)
        {
            var cap = machine.capacity_per_hour;

            if (cap <= 0)
                cap = 1;

            var efficiency = machine.efficiency_percent;

            if (efficiency <= 0)
                efficiency = 100;

            return Math.Max(1, cap * efficiency / 100m);
        }

        private static double ResolveDurationHours(
            string processCode,
            decimal requiredUnits,
            decimal effectiveCapacityPerHour)
        {
            requiredUnits = requiredUnits <= 0 ? 1 : requiredUnits;

            var runHours = requiredUnits / Math.Max(1, effectiveCapacityPerHour);

            var setupMinutes = GetSetupMinutes(processCode);

            return Math.Max(
                0.25,
                (double)runHours + setupMinutes / 60.0);
        }

        private static decimal ResolveRequiredUnits(
            string processCode,
            decimal quantity,
            Dictionary<string, decimal> requiredUnitsByProcessCode)
        {
            processCode = Norm(processCode);

            if (requiredUnitsByProcessCode.TryGetValue(processCode, out var explicitQty) &&
                explicitQty > 0)
            {
                return explicitQty;
            }

            return Math.Max(1, quantity);
        }

        private static int GetSetupMinutes(string processCode)
        {
            return Norm(processCode) switch
            {
                "RALO" => 30,
                "CAT" => 20,
                "IN" => 45,
                "PHU" => 20,
                "CAN" => 20,
                "CAN_MANG" => 20,
                "BOI" => 30,
                "BE" => 25,
                "DUT" => 15,
                "DAN" => 25,
                _ => 15
            };
        }

        private static int GetTurnaroundMinutes(string processCode)
        {
            return Norm(processCode) switch
            {
                "IN" => 30,
                "BOI" => 20,
                _ => 10
            };
        }

        private static int GetHandoffMinutes(string processCode)
        {
            return Norm(processCode) switch
            {
                "IN" => 30,
                "BOI" => 20,
                _ => 10
            };
        }

        private static List<string> NormalizeProcessCodes(IEnumerable<string>? input)
        {
            return (input ?? Array.Empty<string>())
                .SelectMany(x =>
                    (x ?? "")
                    .Split(new[] { ',', ';', '|', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(Norm)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(FullRouteIndex)
                .ToList();
        }

        private static int FullRouteIndex(string? code)
        {
            return Norm(code) switch
            {
                "RALO" => 1,
                "CAT" => 2,
                "IN" => 3,
                "PHU" => 4,
                "CAN" => 5,
                "BOI" => 6,
                "BE" => 7,
                "DUT" => 8,
                "DAN" => 9,
                _ => 999
            };
        }

        private static bool SameCode(string? a, string? b)
        {
            return string.Equals(
                Norm(a),
                Norm(b),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string Norm(string? value)
        {
            return (value ?? "")
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "_")
                .Replace("-", "_");
        }

        private sealed class BusyInterval
        {
            public DateTime Start { get; set; }

            public DateTime End { get; set; }
        }
    }
}