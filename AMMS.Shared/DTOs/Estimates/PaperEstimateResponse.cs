namespace AMMS.Shared.DTOs.Estimates
{
    namespace AMMS.Shared.DTOs.Estimates
    {
        /// <summary>
        /// Response chứa kết quả ước lượng giấy
        /// </summary>
        public class PaperEstimateResponse
        {
            // ==================== THÔNG TIN GIẤY ====================

            public string paper_code { get; set; } = null!;
            public int sheet_width_mm { get; set; }
            public int sheet_height_mm { get; set; }

            // ==================== KÍCH THƯỚC BẢN IN ====================

            public int print_width_mm { get; set; }
            public int print_height_mm { get; set; }

            // ==================== SỐ LƯỢNG ====================

            /// <summary>
            /// Số sản phẩm in được trên 1 tờ giấy
            /// </summary>
            public int n_up { get; set; }

            /// <summary>
            /// Số lượng sản phẩm cần sản xuất
            /// </summary>
            public int quantity { get; set; }

            /// <summary>
            /// Số tờ cơ bản (chưa tính hao hụt)
            /// </summary>
            public int sheets_base { get; set; }

            // ==================== HAO HỤT CHI TIẾT ====================

            /// <summary>
            /// Hao hụt công đoạn in
            /// </summary>
            public int waste_printing { get; set; }

            /// <summary>
            /// Hao hụt công đoạn bế
            /// </summary>
            public int waste_die_cutting { get; set; }

            /// <summary>
            /// Hao hụt công đoạn bồi
            /// </summary>
            public int waste_mounting { get; set; }

            /// <summary>
            /// Hao hụt công đoạn phủ
            /// </summary>
            public int waste_coating { get; set; }

            /// <summary>
            /// Hao hụt công đoạn cán màng
            /// </summary>
            public int waste_lamination { get; set; }

            /// <summary>
            /// Hao hụt công đoạn dán
            /// </summary>
            public int waste_gluing { get; set; }

            /// <summary>
            /// Tổng hao hụt
            /// </summary>
            public int total_waste { get; set; }

            // ==================== TỔNG KẾT ====================

            /// <summary>
            /// Tổng số tờ cần thiết (bao gồm hao hụt)
            /// </summary>
            public int sheets_with_waste { get; set; }

            /// <summary>
            /// Phần trăm hao hụt (%)
            /// </summary>
            public decimal waste_percent { get; set; }
        }
    }
}
