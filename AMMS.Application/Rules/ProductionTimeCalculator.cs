using AMMS.Infrastructure.Entities;
using AMMS.Shared.DTOs.Enums;
using AMMS.Shared.DTOs.Estimates;
using CloudinaryDotNet.Actions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AMMS.Application.Rules
{
    public class ProductionTimeCalculator
    {
        public static int CalculateProductionDays(int sheetsWithWaste, int productQuantity, List<ProcessType> processes, List<machine> machines)
        {
            if (!machines.Any() || !processes.Any())
            {
                return SystemParameters.DEFAULT_PRODUCTION_DAYS;
            }

            var processDays = new List<double>();

            foreach (var process in processes)
            {
                string processName = MapProcessTypeToName(process);

                var processMachines = machines
                    .Where(m => m.process_name == processName && m.is_active == true)
                    .ToList();

                if (!processMachines.Any())
                {
                    continue;
                }

                decimal totalDailyCapacity = processMachines.Sum(m => m.daily_capacity);

                if (totalDailyCapacity <= 0)
                {
                    continue;
                }

                int requiredQty = GetRequiredQuantity(process, sheetsWithWaste, productQuantity);

                // Tính số ngày cần
                double daysNeeded = (double)requiredQty / (double)totalDailyCapacity;

                daysNeeded = Math.Max(1, Math.Ceiling(daysNeeded));

                processDays.Add(daysNeeded);
            }

            if (!processDays.Any())
            {
                return SystemParameters.DEFAULT_PRODUCTION_DAYS;
            }

            int bottleneckDays = (int)Math.Ceiling(processDays.Max());

            int totalDays = (int)Math.Ceiling(bottleneckDays * 1.3);

            totalDays = Math.Max(2, Math.Min(30, totalDays));

            return totalDays;
        }

        public static Dictionary<string, ProcessTimeDetail> CalculateDetailedProcessTime(int sheetsWithWaste, int productQuantity, List<ProcessType> processes, List<machine> machines)
        {
            var details = new Dictionary<string, ProcessTimeDetail>();

            foreach (var process in processes)
            {
                string processName = MapProcessTypeToName(process);

                var processMachines = machines
                    .Where(m => m.process_name == processName && m.is_active == true)
                    .ToList();

                if (!processMachines.Any())
                {
                    continue;
                }

                decimal totalDailyCapacity = processMachines.Sum(m => m.daily_capacity);
                int requiredQty = GetRequiredQuantity(process, sheetsWithWaste, productQuantity);

                double daysNeeded = totalDailyCapacity > 0 ? Math.Ceiling((double)requiredQty / (double)totalDailyCapacity) : 0;

                details[processName] = new ProcessTimeDetail
                {
                    ProcessName = processName,
                    RequiredQuantity = requiredQty,
                    TotalDailyCapacity = Math.Round(totalDailyCapacity, 0),
                    DaysNeeded = daysNeeded,
                    MachineCount = processMachines.Sum(m => m.quantity),
                    CapacityPerHour = (decimal)Math.Round(processMachines.Average(m => m.capacity_per_hour), 0),
                    IsBottleneck = false // Set sau
                };
            }

            // Xác định công đoạn chậm nhất
            if (details.Any())
            {
                double maxDays = details.Values.Max(d => d.DaysNeeded);
                foreach (var detail in details.Values.Where(d => d.DaysNeeded == maxDays))
                {
                    detail.IsBottleneck = true;
                }
            }

            return details;
        }

        // Map ProcessType sang tên công đoạn trong DB
        private static string MapProcessTypeToName(ProcessType processType)
        {
            return processType switch
            {
                ProcessType.IN => "In",
                ProcessType.BE => "Bế",
                ProcessType.BOI => "Bồi",
                ProcessType.PHU => "Phủ",
                ProcessType.CAN_MANG => "Cán",
                ProcessType.DAN => "Dán",
                ProcessType.DUT => "Dứt",
                ProcessType.RALO => "Ralo",
                ProcessType.CAT => "Cắt",
                _ => processType.ToString()

            };
        }

        /// <summary>
        /// Xác định số lượng cần xử lý
        /// Công đoạn trước Dứt: tính theo TỜ
        /// Công đoạn từ Dứt trở đi: tính theo SẢN PHẨM
        /// </summary>
        private static int GetRequiredQuantity(ProcessType processType, int sheets, int products)
        {
            return processType switch
            {
                // Các công đoạn xử lý TỜ GIẤY
                ProcessType.IN => sheets,
                ProcessType.PHU => sheets,
                ProcessType.CAN_MANG => sheets,
                ProcessType.BOI => sheets,
                ProcessType.BE => sheets,
                ProcessType.RALO => sheets,
                ProcessType.DUT => products,
                ProcessType.DAN => products,
                ProcessType.CAT => products,
                // Mặc định
                _ => sheets
            };
        }
    }
}
