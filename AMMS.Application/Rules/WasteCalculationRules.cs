using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AMMS.Shared.DTOs.Enums;

namespace AMMS.Application.Rules
{
    public static class WasteCalculationRules
    {
        // ==================== HAO HỤT CÔNG ĐOẠN IN ====================

        /// <summary>
        /// Hao hụt cơ bản theo loại sản phẩm (tờ)
        /// </summary>
        public static class PrintingWaste
        {
            // Gạch
            public const int GACH_1MAU = 50;
            public const int GACH_XUAT_KHAU_DON_GIAN = 120;
            public const int GACH_XUAT_KHAU_TERACON = 200;
            public const int GACH_NOI_DIA_4SP = 150;
            public const int GACH_NOI_DIA_6SP = 180;

            // Hộp màu
            public const int HOP_MAU_1LUOT_DON_GIAN = 200;
            public const int HOP_MAU_1LUOT_THUONG = 230;
            public const int HOP_MAU_1LUOT_KHO = 250;
            public const int HOP_MAU_AQUA_DOI = 450;
            public const int HOP_MAU_2LUOT_1 = 230;  // Lượt 1
            public const int HOP_MAU_2LUOT_2 = 100;  // Lượt 2

            // Thêm cho mỗi cao bản (chỉ áp dụng hộp màu)
            public const int PER_PLATE = 10;

            public static int GetBaseWaste(ProductTypeCode productType)
            {
                return productType switch
                {
                    ProductTypeCode.GACH_1MAU => GACH_1MAU,
                    ProductTypeCode.GACH_XUAT_KHAU_DON_GIAN => GACH_XUAT_KHAU_DON_GIAN,
                    ProductTypeCode.GACH_XUAT_KHAU_TERACON => GACH_XUAT_KHAU_TERACON,
                    ProductTypeCode.GACH_NOI_DIA_4SP => GACH_NOI_DIA_4SP,
                    ProductTypeCode.GACH_NOI_DIA_6SP => GACH_NOI_DIA_6SP,
                    ProductTypeCode.HOP_MAU_1LUOT_DON_GIAN => HOP_MAU_1LUOT_DON_GIAN,
                    ProductTypeCode.HOP_MAU_1LUOT_THUONG => HOP_MAU_1LUOT_THUONG,
                    ProductTypeCode.HOP_MAU_1LUOT_KHO => HOP_MAU_1LUOT_KHO,
                    ProductTypeCode.HOP_MAU_AQUA_DOI => HOP_MAU_AQUA_DOI,
                    ProductTypeCode.HOP_MAU_2LUOT => HOP_MAU_2LUOT_1 + HOP_MAU_2LUOT_2,
                    _ => 200
                };
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN BẾ ====================

        /// <summary>
        /// Hao hụt công đoạn bế (tờ)
        /// Chưa tính phần giấy xắp của công đoạn bồi chuẩn bị cho bế
        /// </summary>
        public static class DieCuttingWaste
        {
            public static int Calculate(int sheetsBase)
            {
                return sheetsBase switch
                {
                    < 5000 => 20,
                    < 20000 => 30,
                    <= 40000 => 40,
                    _ => 40
                };
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN BỒI ====================

        /// <summary>
        /// Hao hụt công đoạn bồi (tờ)
        /// </summary>
        public static class MountingWaste
        {
            public static int Calculate(int sheetsBase)
            {
                return sheetsBase switch
                {
                    < 5000 => 20,
                    < 20000 => 30,
                    <= 40000 => 40,
                    _ => 40
                };
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN PHỦ ====================

        /// <summary>
        /// Hao hụt công đoạn phủ (tờ)
        /// Keo nước: 0 (có thể phủ lại)
        /// Keo dầu: tính theo số lượng tờ in
        /// </summary>
        public static class CoatingWaste
        {
            public static int Calculate(int sheetsBase, CoatingType coatingType)
            {
                if (coatingType == CoatingType.KEO_NUOC)
                    return 0;

                if (coatingType == CoatingType.KEO_DAU)
                    return sheetsBase < 10000 ? 20 : 30;

                return 0;
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN CÁN MÀNG ====================

        /// <summary>
        /// Hao hụt công đoạn cán màng (tờ)
        /// </summary>
        public static class LaminationWaste
        {
            public static int Calculate(int sheetsBase)
            {
                return sheetsBase < 10000 ? 20 : 30;
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN DÁN ====================

        /// <summary>
        /// Hao hụt công đoạn dán (hộp)
        /// Trung bình 20-30 hộp, chưa tính phần giấy xắp của công đoạn dứt
        /// </summary>
        public static class GluingWaste
        {
            public static int Calculate(int quantity)
            {
                // Tính theo số lượng sản phẩm (hộp)
                return quantity switch
                {
                    < 100 => 10,
                    < 500 => 15,
                    < 2000 => 20,
                    _ => 25
                };
            }
        }

        // ==================== HAO HỤT CÔNG ĐOẠN DỨT ====================

        /// <summary>
        /// Công đoạn dứt: Không để hao hụt mà tính bằng công đoạn bế
        /// </summary>
        public static class TrimWaste
        {
            // Không tính hao hụt riêng, đã tính trong Bế
            public const int NO_WASTE = 0;
        }

        // ==================== HAO HỤT CÔNG ĐOẠN CẮT ====================

        /// <summary>
        /// Công đoạn cắt: Không tính hao hụt
        /// Nếu cắt nhầm phải ralo bù vì mỗi lần hỏng là hỏng trên 100 tờ
        /// </summary>
        public static class CuttingWaste
        {
            public const int NO_WASTE = 0;
        }
    }

    /// <summary>
    /// Định mức vật liệu và giá
    /// Dựa trên tài liệu "Định mức và cách tính"
    /// </summary>
    public static class MaterialRates
    {
        // ==================== ĐỊNH MỨC MỰC IN ====================

        /// <summary>
        /// Định mức mực in (kg/m2)
        /// </summary>
        public static class InkRates
        {
            public const decimal GACH_NOI_DIA = 0.0003m;           // Gạch nội địa
            public const decimal GACH_XUAT_KHAU_DON_GIAN = 0.0003m; // Gạch XK đơn giản
            public const decimal HOP_MAU = 0.0009m;                 // Hộp màu
            public const decimal GACH_NHIEU_MAU = 0.001m;          // Gạch nhiều màu

            public static decimal GetRate(ProductTypeCode productType)
            {
                return productType switch
                {
                    ProductTypeCode.GACH_1MAU => GACH_NOI_DIA,
                    ProductTypeCode.GACH_XUAT_KHAU_DON_GIAN => GACH_XUAT_KHAU_DON_GIAN,
                    ProductTypeCode.GACH_XUAT_KHAU_TERACON => GACH_NHIEU_MAU,
                    ProductTypeCode.GACH_NOI_DIA_4SP => GACH_NHIEU_MAU,
                    ProductTypeCode.GACH_NOI_DIA_6SP => GACH_NHIEU_MAU,
                    _ when productType.ToString().StartsWith("HOP_MAU") => HOP_MAU,
                    _ => HOP_MAU
                };
            }
        }

        // ==================== ĐỊNH MỨC KEO PHỦ ====================

        /// <summary>
        /// Định mức keo phủ (kg/m2 tờ in)
        /// Tính theo dung dịch keo đã pha
        /// </summary>
        public static class CoatingGlueRates
        {
            public const decimal KEO_NUOC = 0.003m;  // Keo nước
            public const decimal KEO_DAU = 0.004m;   // Keo dầu

            public static decimal GetRate(CoatingType coatingType)
            {
                return coatingType switch
                {
                    CoatingType.KEO_NUOC => KEO_NUOC,
                    CoatingType.KEO_DAU => KEO_DAU,
                    _ => 0m
                };
            }
        }

        // ==================== ĐỊNH MỨC KEO BỒI ====================

        /// <summary>
        /// Định mức keo bồi (kg/m2 tờ sóng)
        /// Tính theo dung dịch keo đã pha
        /// </summary>
        public static class MountingGlueRates
        {
            public const decimal RATE = 0.004m;
        }

        // ==================== ĐỊNH MỨC MÀNG CÁN ====================

        /// <summary>
        /// Định mức màng cán (kg/m2 tờ in)
        /// 12g màng + 5g keo = 17g/m2 = 0.017kg/m2
        /// </summary>
        public static class LaminationRates
        {
            public const decimal RATE_12MIC = 0.017m;  // 12 micron
        }
    }

    /// <summary>
    /// Giá vật liệu (VND)
    /// Có thể load từ database hoặc config
    /// </summary>
    public static class MaterialPrices
    {
        // Giá mực (VND/kg)
        public const decimal INK_PRICE_PER_KG = 150000m;

        // Giá keo phủ (VND/kg)
        public const decimal COATING_GLUE_KEO_NUOC_PER_KG = 70000m;
        public const decimal COATING_GLUE_KEO_DAU_PER_KG = 80000m;

        // Giá keo bồi (VND/kg)
        public const decimal MOUNTING_GLUE_PER_KG = 60000m;

        // Giá màng (VND/kg)
        public const decimal LAMINATION_PER_KG = 200000m;

        public static decimal GetCoatingGluePrice(CoatingType coatingType)
        {
            return coatingType switch
            {
                CoatingType.KEO_NUOC => COATING_GLUE_KEO_NUOC_PER_KG,
                CoatingType.KEO_DAU => COATING_GLUE_KEO_DAU_PER_KG,
                _ => 0m
            };
        }
    }

    /// <summary>
    /// Các tham số khác cho hệ thống
    /// </summary>
    public static class SystemParameters
    {
        // Khấu hao máy móc và chi phí khác (%)
        public const decimal OVERHEAD_PERCENT = 5m;  // Giảm từ 10% xuống 5%

        // Số ngày làm việc dự kiến để hoàn thành đơn hàng
        public const int DEFAULT_PRODUCTION_DAYS = 5;

        // Ngưỡng rush order (số ngày giao sớm hơn dự kiến)
        public const int RUSH_THRESHOLD_DAYS = 1;

        // Phần trăm phí gấp theo số ngày giao sớm
        public static decimal GetRushPercent(int daysEarly)
        {
            return daysEarly switch
            {
                1 => 5m,
                >= 2 and <= 3 => 20m,
                >= 4 => 40m,
                _ => 0m
            };
        }

        // Ngưỡng đơn hàng nhỏ (tờ)
        //public const int SMALL_ORDER_THRESHOLD = 500;
        //public const int FULL_WASTE_THRESHOLD = 2000;
    }
}
