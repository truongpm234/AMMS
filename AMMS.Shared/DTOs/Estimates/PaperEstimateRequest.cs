using AMMS.Shared.DTOs.Enums;

namespace AMMS.Shared.DTOs.Estimates
{
    public class PaperEstimateRequest
    {
        /// <summary>
        /// Mã giấy trong hệ thống
        /// </summary>
        public string paper_code { get; set; } = null!;

        /// <summary>
        /// Số lượng sản phẩm cần sản xuất
        /// </summary>
        public int quantity { get; set; }

        // ==================== KÍCH THƯỚC SẢN PHẨM (mm) ====================

        /// <summary>
        /// Chiều dài hộp (mm)
        /// </summary>
        public int length_mm { get; set; }

        /// <summary>
        /// Chiều rộng hộp (mm)
        /// </summary>
        public int width_mm { get; set; }

        /// <summary>
        /// Chiều cao hộp (mm)
        /// </summary>
        public int height_mm { get; set; }

        // ==================== TÙY CHỈNH THÊM ====================

        /// <summary>
        /// Tab dán (glue flap) - Mặc định 20mm
        /// Đây là phần giấy dư để dán hộp
        /// </summary>
        public int glue_tab_mm { get; set; } = 15;

        /// <summary>
        /// Chừa xén (bleed) - Mặc định 1mm mỗi bên
        /// Phần dư ra ngoài để cắt sau khi in
        /// </summary>
        public int bleed_mm { get; set; } = 1;

        /// <summary>
        /// Hộp 1 chiều (chỉ có nắp HOẶC đáy)
        /// False = Hộp cơ bản có cả nắp và đáy (mặc định)
        /// </summary>
        public bool is_one_side_box { get; set; } = false;

        // ==================== THÔNG TIN SẢN XUẤT ====================

        /// <summary>
        /// Loại sản phẩm in
        /// VD: "HOP_MAU_1LUOT_THUONG", "GACH_NOI_DIA_4SP"
        /// </summary>
        public string product_type { get; set; } = "";

        public string? form_product { get; set; }

        /// <summary>
        /// Số cao bản (plates) - Chỉ áp dụng cho hộp màu
        /// Mỗi cao bản thêm 10 tờ hao hụt
        /// </summary>
        public int number_of_plates { get; set; } = 0;

        /// <summary>
        /// Danh sách công đoạn sản xuất, cách nhau bởi dấu phẩy
        /// VD: "IN,BE,BOI,DAN,PHU,CAN_MANG"
        /// </summary>
        public string production_processes { get; set; } = "IN";

        /// <summary>
        /// Loại phủ: "NONE", "KEO_NUOC", "KEO_DAU"
        /// </summary>
        public string coating_type { get; set; } = "NONE";
    }
}
