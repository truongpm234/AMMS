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
        public static class PrintingWaste
        {
            public const int GACH_1MAU = 50;
            public const int GACH_XUAT_KHAU_DON_GIAN = 120;
            public const int GACH_XUAT_KHAU_TERACON = 200;
            public const int GACH_NOI_DIA_4SP = 150;
            public const int GACH_NOI_DIA_6SP = 180;

            public const int HOP_MAU_1LUOT_DON_GIAN = 200;
            public const int HOP_MAU_1LUOT_THUONG = 230;
            public const int HOP_MAU_1LUOT_KHO = 250;
            public const int HOP_MAU_AQUA_DOI = 450;
            public const int HOP_MAU_2LUOT_1 = 230;  
            public const int HOP_MAU_2LUOT_2 = 100;  

            public const int PER_PLATE = 10;

            public static int GetBaseWaste(string productTypeCode)
            {
                productTypeCode = (productTypeCode ?? "").Trim();

                if (Enum.TryParse<ProductTypeCodeOfGach>(productTypeCode, true, out var gach))
                {
                    return gach switch
                    {
                        ProductTypeCodeOfGach.GACH_1MAU => GACH_1MAU,
                        ProductTypeCodeOfGach.GACH_XUAT_KHAU_DON_GIAN => GACH_XUAT_KHAU_DON_GIAN,
                        ProductTypeCodeOfGach.GACH_XUAT_KHAU_TERACON => GACH_XUAT_KHAU_TERACON,
                        ProductTypeCodeOfGach.GACH_NOI_DIA_4SP => GACH_NOI_DIA_4SP,
                        ProductTypeCodeOfGach.GACH_NOI_DIA_6SP => GACH_NOI_DIA_6SP,
                        _ => 200
                    };
                }

                if (Enum.TryParse<ProductTypeCodeOfHop_mau>(productTypeCode, true, out var hop))
                {
                    return hop switch
                    {
                        ProductTypeCodeOfHop_mau.HOP_MAU_1LUOT_DON_GIAN => HOP_MAU_1LUOT_DON_GIAN,
                        ProductTypeCodeOfHop_mau.HOP_MAU_1LUOT_THUONG => HOP_MAU_1LUOT_THUONG,
                        ProductTypeCodeOfHop_mau.HOP_MAU_1LUOT_KHO => HOP_MAU_1LUOT_KHO,
                        ProductTypeCodeOfHop_mau.HOP_MAU_AQUA_DOI => HOP_MAU_AQUA_DOI,
                        ProductTypeCodeOfHop_mau.HOP_MAU_2LUOT => HOP_MAU_2LUOT_1 + HOP_MAU_2LUOT_2,
                        _ => 200
                    };
                }

                return 200;
            }

        }

        public class DieCuttingWaste
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

        public class MountingWaste
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

        public class CoatingWaste
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

        public class LaminationWaste
        {
            public static int Calculate(int sheetsBase)
            {
                return sheetsBase < 10000 ? 20 : 30;
            }
        }

        public class GluingWaste
        {
            public static int Calculate(int quantity)
            {
                return quantity switch
                {
                    < 100 => 10,
                    < 500 => 15,
                    < 2000 => 20,
                    _ => 25
                };
            }
        }

        public class TrimWaste
        {
            public const int NO_WASTE = 0;
        }

        public class CuttingWaste
        {
            public const int NO_WASTE = 0;
        }
    }

    public class MaterialRates
    {
        public class InkRates
        {
            public const decimal GACH_NOI_DIA = 0.0003m;           
            public const decimal GACH_XUAT_KHAU_DON_GIAN = 0.0003m; 
            public const decimal HOP_MAU = 0.0009m;                
            public const decimal GACH_NHIEU_MAU = 0.001m;          

            public static decimal GetRate(string productTypeCode)
            {
                productTypeCode = (productTypeCode ?? "").Trim();

                if (Enum.TryParse<ProductTypeCodeOfGach>(productTypeCode, true, out var gach))
                {
                    return gach switch
                    {
                        ProductTypeCodeOfGach.GACH_1MAU => GACH_NOI_DIA,
                        ProductTypeCodeOfGach.GACH_XUAT_KHAU_DON_GIAN => GACH_XUAT_KHAU_DON_GIAN,
                        ProductTypeCodeOfGach.GACH_XUAT_KHAU_TERACON => GACH_NHIEU_MAU,
                        ProductTypeCodeOfGach.GACH_NOI_DIA_4SP => GACH_NHIEU_MAU,
                        ProductTypeCodeOfGach.GACH_NOI_DIA_6SP => GACH_NHIEU_MAU,
                        _ => HOP_MAU
                    };
                }
                return HOP_MAU;
            }

        }

        public class CoatingGlueRates
        {
            public const decimal KEO_NUOC = 0.003m;  
            public const decimal KEO_DAU = 0.004m;

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


        public class MountingGlueRates
        {
            public const decimal RATE = 0.004m;
        }

        public class LaminationRates
        {
            public const decimal RATE_12MIC = 0.017m;
        }
    }

    public class MaterialPrices
    {
        public const decimal INK_PRICE_PER_KG = 150000m;

        public const decimal COATING_GLUE_KEO_NUOC_PER_KG = 70000m;
        public const decimal COATING_GLUE_KEO_DAU_PER_KG = 80000m;

        public const decimal MOUNTING_GLUE_PER_KG = 60000m;

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

    public class SystemParameters
    {
        public const decimal OVERHEAD_PERCENT = 5m;

        public const int DEFAULT_PRODUCTION_DAYS = 5;

        public const int RUSH_THRESHOLD_DAYS = 1;

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
    }
}
