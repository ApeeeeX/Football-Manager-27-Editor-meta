namespace Fm27Editor.App;

public static partial class Loc
{
    static partial void RegisterPlayers(Dictionary<string, string> m)
    {
        // Search, Filters, Player Actions & Bulk UI
        m["Search player name or ID..."] = "Tìm tên hoặc ID cầu thủ...";
        m["Search player or ID…"] = "Tìm cầu thủ hoặc ID…";
        m["Universal search: player, club, stadium, staff, competition, award or ID"] =
            "Tìm nhanh: cầu thủ, CLB, sân vận động, nhân viên, giải đấu, giải thưởng hoặc ID";
        m["Reset"] = "Đặt lại";
        m["Clear"] = "Xóa";
        m["Save"] = "Lưu";
        m["Save changes"] = "Lưu thay đổi";
        m["Legend Players"] = "Cầu thủ huyền thoại";
        m["Make a Legend"] = "Hóa thành huyền thoại";
        m["Choose a Legend"] = "Chọn huyền thoại";
        m["Search legends…"] = "Tìm huyền thoại…";
        m["Use preset"] = "Chọn mẫu";
        m["Clear legend"] = "Bỏ mẫu huyền thoại";
        m["{0} selected. Save changes to apply."] = "Đã chọn {0}. Bấm Lưu để áp dụng.";
        m["In-Match Ability Modifier"] = "Chỉnh chỉ số trong trận";
        m["In-Match Ability Modifier (Experimental)"] = "Chỉnh chỉ số trong trận (thử nghiệm)";
        m["Edit attributes and condition for players in an active match."] = "Chỉnh chỉ số và thể lực cầu thủ trong trận đang diễn ra.";
        m["Select match…"] = "Chọn trận…";
        m["Select scope…"] = "Chọn phạm vi…";
        m["Choose target…"] = "Chọn đối tượng…";
        m["All positions"] = "Mọi vị trí";
        m["Minimum familiarity"] = "Độ thuần thục tối thiểu";
        m["Max performance attributes"] = "Tối đa chỉ số thi đấu";
        m["Condition (%)"] = "Thể lực (%)";
        m["Match and target"] = "Trận và đối tượng";
        m["Attribute overrides"] = "Tùy chỉnh chỉ số";
        m["Scan matches"] = "Dò trận đang mở";
        m["Apply to match"] = "Áp dụng trong trận";
        m["Scanning for active matches…"] = "Đang dò trận đang mở…";
        m["No active match found for this club"] = "Chưa tìm thấy trận đang mở của CLB này";
        m["Checking the match roster…"] = "Đang kiểm tra đội hình trong trận…";
        m["The match changed. Scan for matches again."] = "Trận đã thay đổi. Hãy dò lại trận đang mở.";
        m["Applying to match…"] = "Đang áp dụng trong trận…";
        m["Updated {0} player(s) in this match."] = "Đã cập nhật {0} cầu thủ trong trận này.";
        m["No match values needed changing"] = "Các giá trị trong trận đã đúng mức đã chọn";
        m["No matching results"] = "Không tìm thấy kết quả phù hợp";
        m["The match changed. Pause it and scan again."] = "Trận đã thay đổi. Hãy tạm dừng trận rồi dò lại.";
        m["Both teams"] = "Cả hai đội";
        m["Clear selection"] = "Bỏ chọn";
        m["Find a match player…"] = "Tìm cầu thủ trong trận…";
        m["Filter by nation"] = "Lọc theo quốc tịch";
        m["All nations"] = "Mọi quốc tịch";
        m["Match players"] = "Cầu thủ trong trận";
        m["Match presets"] = "Preset trong trận";
        m["All in One"] = "Tổng hợp";
        m["Restore match condition to 100%."] = "Hồi thể lực trong trận lên 100%.";
        m["Set match morale to 20."] = "Đưa tinh thần trong trận lên 20.";
        m["Restore match condition and morale, and raise performance attributes."] = "Hồi thể lực, tinh thần và tăng chỉ số thi đấu.";
        m["Set match condition to 50%, morale to 1 and performance attributes to 1."] = "Đưa thể lực về 50%, tinh thần và chỉ số thi đấu về 1.";
        m["Condition and morale"] = "Thể lực và tinh thần";
        m["Set all attributes"] = "Đặt toàn bộ chỉ số";
        m["Set all attributes to {0}"] = "Đặt toàn bộ chỉ số về {0}";
        m["Clear changes"] = "Xóa thay đổi";
        m["{0} players selected"] = "Đã chọn {0} cầu thủ";
        m["{0} players selected ({1} outside filters)"] = "Đã chọn {0} cầu thủ ({1} ngoài bộ lọc)";
        m["Manager Unlockables (Experimental)"] = "Tùy chọn HLV (thử nghiệm)";
        m["Manager Unlockables"] = "Tùy chọn HLV";
        m["All Players Interested"] = "Mọi cầu thủ đều quan tâm";
        m["Become Unsackable"] = "Không bị sa thải";
        m["Makes players interested in joining this manager's club."] = "Khiến cầu thủ quan tâm đến việc gia nhập CLB của HLV này.";
        m["Prevents this manager from being sacked."] = "Ngăn HLV này bị sa thải.";
        m["All position groups"] = "Mọi nhóm vị trí";
        m["Goalkeepers (GK)"] = "Thủ môn (GK)";
        m["Central Defenders (DC)"] = "Trung vệ (DC)";
        m["Wide Defenders (D/WB RL)"] = "Hậu vệ biên (D/WB RL)";
        m["Central Midfielders (DM/M/AM C)"] = "Tiền vệ trung tâm (DM/M/AM C)";
        m["Wide Midfielders (M/AM RL)"] = "Tiền vệ cánh (M/AM RL)";
        m["Strikers (ST)"] = "Tiền đạo (ST)";
        m["Copy attributes, positions, personality and ability from a historical player preset."] = "Chép chỉ số, vị trí, tính cách và năng lực từ một mẫu cầu thủ huyền thoại.";
        m["{0}, age {1}, CA {2} / PA {3}"] = "{0}, {1} tuổi, CA {2} / PA {3}";
        m["Applied {0}. Continue in FM to see updates."] = "Đã áp dụng {0}. Bấm Continue trong FM để xem thay đổi.";
        m["Save loan"] = "Lưu hợp đồng mượn";
        m["Apply"] = "Áp dụng";
        m["Filter by name"] = "Lọc theo tên";
        m["Filter by {0}"] = "Lọc theo {0}";
        m["No filters active"] = "Không có bộ lọc nào đang bật";
        m["No filters set"] = "Chưa đặt bộ lọc nào";
        m["{0} filter(s)"] = "{0} bộ lọc";
        m["{0} filter(s) set"] = "Đã đặt {0} bộ lọc";
        m[" (max)"] = " (tối đa)";
        m[" (min)"] = " (tối thiểu)";
        m[" ({0} players)"] = " ({0} cầu thủ)";
        m[" · usually {0}"] = " · thường là {0}";
        m[" — {0:N0} of {1:N0} players shown ({2:N0} hidden)"] = " — Đang hiện {0:N0} / {1:N0} cầu thủ (ẩn {2:N0})";
        m["Default Attr >="] = "Chỉ số mặc định >=";
        m["PA … to …"] = "PA … đến …";
        m["Select an attribute"] = "Chọn một chỉ số";
        m["No player with that id in this list."] = "Không có cầu thủ nào mang ID đó trong danh sách này.";
        m["Select a player first"] = "Hãy chọn một cầu thủ trước";
        m["Only male players and men's clubs are supported."] = "Chỉ hỗ trợ cầu thủ nam và CLB nam.";
        m["No limit"] = "Không giới hạn";
        m["Include every row"] = "Chọn toàn bộ các dòng";
        m["{0} of {1} selected"] = "Đã chọn {0} / {1}";
        m["{0} player(s)"] = "{0} cầu thủ";
        m["{0} teams"] = "{0} đội";
        m["{0:N0} players"] = "{0:N0} cầu thủ";
        m["{0:N0} players (Advanced Search)"] = "{0:N0} cầu thủ (Tìm kiếm nâng cao)";
        m["All {0:N0} players{1}"] = "Tất cả {0:N0} cầu thủ{1}";
        m["Other players"] = "Các cầu thủ khác";
        m["Other ({0})"] = "Khác ({0})";
        m["Export"] = "Xuất dữ liệu";
        m["Import"] = "Nhập dữ liệu";
        m["Export attributes"] = "Xuất chỉ số";
        m["Import attributes"] = "Nhập chỉ số";
        m["Export players"] = "Xuất danh sách cầu thủ";
        m["Exported {0:N0} players"] = "Đã xuất {0:N0} cầu thủ";
        m["Attributes exported"] = "Đã xuất chỉ số";
        m["Export failed: {0}"] = "Xuất thất bại: {0}";
        m["Import failed: {0}"] = "Nhập thất bại: {0}";
        m["Expected Attribute,Value header"] = "Cần có dòng tiêu đề Attribute,Value";
        m["Invalid or duplicate attribute"] = "Chỉ số không hợp lệ hoặc bị trùng";
        m["File too large"] = "Tệp quá lớn";
        m["The file must contain every attribute; attribute values are 1–20 and personality values are 0–20"] =
            "Tệp phải chứa đầy đủ mọi chỉ số; chỉ số chuyên môn từ 1–20 và chỉ số tính cách từ 0–20";
        m["Player changed while choosing the file"] = "Cầu thủ đã thay đổi trong lúc chọn tệp";
        m["{0} unsaved change(s); {1} unknown personality value(s) skipped"] =
            "{0} thay đổi chưa lưu; đã bỏ qua {1} giá trị tính cách không rõ";
        m["{0} attribute(s)"] = "{0} chỉ số";
        m["{0} position(s)"] = "{0} vị trí";
        m["Position {0}"] = "Vị trí {0}";
        m["Position {0}: enter a non-negative whole number"] = "Vị trí {0}: nhập số nguyên không âm";
        m["Unknown ({0})"] = "Không rõ ({0})";
        m["Unknown (n)"] = "Không rõ (n)";
        m["person {0}"] = "người {0}";
        m["player id {0}"] = "ID cầu thủ {0}";
        m["id {0}"] = "ID {0}";
        m["Person {0}"] = "Cá nhân {0}";
        m["Unique ID"] = "ID duy nhất";
        m["Short Name"] = "Tên ngắn";
        m["Full Name"] = "Họ và tên";
        m["Three-Letter Name"] = "Tên viết tắt 3 chữ";
        m["Local Region"] = "Khu vực địa phương";
        m["Gender: {0}"] = "Giới tính: {0}";
        m["{0} · {1} · {2} yrs"] = "{0} · {1} · {2} tuổi";

        // Presets, Freeze, Retirement, Speed, Golden Gen helpers
        m["Max attributes"] = "Tối đa chỉ số";
        m["Heal all"] = "Hồi phục tất cả";
        m["Lift bans"] = "Xóa án treo giò";
        m["Lift loan limits"] = "Gỡ giới hạn cho mượn";
        m["Lift nationality limits"] = "Gỡ giới hạn quốc tịch";
        m["Player unfrozen"] = "Đã bỏ đóng băng cầu thủ";
        m["Freezer: {0}"] = "Đóng băng: {0}";
        m["Freezer — {0}"] = "Đóng băng — {0}";
        m["Prevent retirement: {0}"] = "Ngăn giải nghệ: {0}";
        m["Nothing to change: already set to keep playing"] = "Không có gì để đổi: đã đặt tiếp tục thi đấu";
        m["{0}: already set to keep playing"] = "{0}: đã được đặt tiếp tục thi đấu";
        m["{0} will keep playing. Continue in FM to see updates."] = "{0} sẽ tiếp tục thi đấu. Nhấn Continue trong FM để thấy cập nhật.";
        m["{0} will not retire on {1}. Continue in FM to see updates."] = "{0} sẽ không giải nghệ vào ngày {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Development Speed: {0}"] = "Tốc độ phát triển: {0}";
        m["Development speed: {0}"] = "Tốc độ phát triển: {0}";
        m["Development speed: already {0}."] = "Tốc độ phát triển: đã là {0}.";
        m["Development speed removed for {0}."] = "Đã xóa tốc độ phát triển cho {0}.";
        m["Development speed set to {0} for {1}."] = "Đã đặt tốc độ phát triển {0} cho {1}.";
        m["Development speed {0} for {1}. Continue in FM to see updates."] =
            "Tốc độ phát triển {0} cho {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Development speed: {0} set, {1} failed ({2})."] = "Tốc độ phát triển: đã đặt {0}, thất bại {1} ({2}).";
        m["Development speed: enter {0} to {1} with at most one decimal (1 = normal)."] =
            "Tốc độ phát triển: nhập từ {0} đến {1} với tối đa 1 chữ số thập phân (1 = bình thường).";
        m["Any speed from 1.1x to 20x in steps of 0.1; 1 means normal speed. Press Enter to apply."] =
            "Tốc độ bất kỳ từ 1.1x đến 20x theo bước 0.1; 1 là bình thường. Nhấn Enter để áp dụng.";
        m["Any speed from {0}x to {1}x in steps of 0.1; 1 means normal speed."] =
            "Tốc độ từ {0}x đến {1}x theo bước 0.1; 1 là bình thường.";
        m["Speed, e.g. 2.5"] = "Tốc độ, vd: 2.5";
        m["normal"] = "bình thường";
        m["No players sped up"] = "Chưa có cầu thủ nào được tăng tốc";
        m["Gains changed {0}"] = "Mức tăng đã đổi {0}";
        m["Undo permanent"] = "Hoàn tác vĩnh viễn";
        m["Undo preset"] = "Hoàn tác thiết lập nhanh";
        m["Undo remove"] = "Hoàn tác xóa";
        m["Restore"] = "Khôi phục";
        m["Restored {0} for {1}. Continue in FM to see updates."] = "Đã khôi phục {0} cho {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Team cohesion maxed for {0} player(s). Continue in FM to see updates."] =
            "Đã tối đa Gắn kết đội bóng cho {0} cầu thủ. Nhấn Continue trong FM để thấy cập nhật.";
        m["Team cohesion maxed for {0} player(s); {1} could not be changed."] =
            "Đã tối đa Gắn kết đội bóng cho {0} cầu thủ; không thể đổi {1}.";
        m[", {0} already had one"] = ", {0} đã có sẵn";
        m[", {0:N0} unavailable"] = ", {0:N0} không khả dụng";
        m[", {0} failed ({1})"] = ", {0} thất bại ({1})";
        m["{0}: {1} already has these values, nothing was changed."] = "{0}: {1} đã có các giá trị này, không có gì thay đổi.";
        m["{0}: {1} player(s) already had it or have nothing to change."] = "{0}: {1} cầu thủ đã có sẵn hoặc không có gì để đổi.";
        m["{0} applied to {1}. Continue in FM to see updates."] = "Đã áp dụng {0} cho {1}. Nhấn Continue trong FM để thấy cập nhật.";

        // Contracts, Clauses, Proposals, Loans, Transfers, Swaps, Clone, Call-up
        m["Contract Details"] = "Chi tiết hợp đồng";
        m["Contract Details ({0})"] = "Chi tiết hợp đồng ({0})";
        m["Transfer Details"] = "Chi tiết chuyển nhượng";
        m["Bonuses & Clauses"] = "Tiền thưởng & Điều khoản";
        m["Transfer Clauses"] = "Điều khoản chuyển nhượng";
        m["Transfer Destination"] = "CLB đích chuyển nhượng";
        m["Asking Price (£)"] = "Giá rao bán (£)";
        m["Transfer Fee (£)"] = "Phí chuyển nhượng (£)";
        m["Wages"] = "Tiền lương";
        m["Wage contribution"] = "Đóng góp tiền lương";
        m["Annual Wage"] = "Lương hàng năm";
        m["Monthly Fee"] = "Phí hàng tháng";
        m["Unused Monthly Fee"] = "Phí hàng tháng khi không thi đấu";
        m["Optional Future Fee"] = "Điều khoản mua đứt tùy chọn";
        m["Agreed Buy Price"] = "Giá mua đã thỏa thuận";
        m["Buy-Back Fee"] = "Phí mua lại";
        m["Initial Fee"] = "Phí ban đầu";
        m["Installment"] = "Trả góp";
        m["Instalment"] = "Trả góp";
        m["Per Win"] = "Mỗi trận thắng";
        m["Per Draw"] = "Mỗi trận hòa";
        m["Per Defeat"] = "Mỗi trận thua";
        m["Appearances"] = "Số lần ra sân";
        m["Starts"] = "Số lần đá chính";
        m["Started"] = "Đá chính";
        m["Num. matches"] = "Số trận";
        m["Num. matches fulfilled"] = "Số trận đã hoàn thành";
        m["After League Appearances"] = "Sau số trận ra sân tại giải VĐQG";
        m["After Cup Appearances"] = "Sau số trận ra sân tại Cúp";
        m["After International Appearances"] = "Sau số lần khoác áo ĐTQG";
        m["Sell-on"] = "Phần trăm bán lại";
        m["% Loan Wage Paid By Parent"] = "% lương cho mượn do CLB chủ quản trả";
        m["% Chance Of Friendly"] = "% khả năng đá giao hữu";
        m["Of the profit"] = "Trên phần lợi nhuận";
        m["First Option To Buy"] = "Quyền ưu tiên mua";
        m["Accumulative"] = "Tích lũy";
        m["Write value"] = "Ghi giá trị";
        m["Add bonus or clause"] = "Thêm tiền thưởng hoặc điều khoản";
        m["Save the clause edits first"] = "Hãy lưu chỉnh sửa điều khoản trước";
        m["Reading clauses…"] = "Đang đọc điều khoản…";
        m["Enter whole numbers for the new clause"] = "Nhập số nguyên cho điều khoản mới";
        m["Clause change stopped: {0}"] = "Đã dừng thay đổi điều khoản: {0}";
        m["Contract save stopped: {0}"] = "Đã dừng lưu hợp đồng: {0}";
        m["Saved {0} contract change(s). Continue in FM to see updates."] =
            "Đã lưu {0} thay đổi hợp đồng. Nhấn Continue trong FM để thấy cập nhật.";
        m["{0} unsaved contract change(s)"] = "{0} thay đổi hợp đồng chưa lưu";
        m["{0} clause(s)"] = "{0} điều khoản";
        m["{0}% due"] = "Phải trả {0}%";
        m["{0}% owed{1}"] = "Còn nợ {0}%{1}";
        m["Contract: enter wages from 0 to 3,500,000 and dates like {0}, expiry after start"] =
            "Hợp đồng: nhập lương từ 0 đến 3.500.000 và ngày dạng {0}, ngày hết hạn sau ngày bắt đầu";
        m["Contract: enter wages of 0 or more and dates like {0}, expiry after start"] =
            "Hợp đồng: nhập lương từ 0 trở lên và ngày dạng {0}, ngày hết hạn sau ngày bắt đầu";
        m["Enter the weekly wage of the new contract."] = "Nhập mức lương tuần cho hợp đồng mới.";
        m["Enter the weekly wage."] = "Nhập mức lương tuần.";
        m["Enter the contract end as {0}"] = "Nhập ngày hết hạn hợp đồng dạng {0}";
        m["The player's contract can't be read"] = "Không thể đọc hợp đồng của cầu thủ";
        m["contract club"] = "CLB chủ quản hợp đồng";
        m[", signed for {0}"] = ", ký hợp đồng với {0}";
        m["Signed"] = "Đã ký";
        m["Signed for"] = "Đã ký cho";
        m["Signed for {0}. Set the new contract terms; Continue in FM to see it."] =
            "Đã ký cho {0}. Hãy thiết lập điều khoản hợp đồng mới; nhấn Continue trong FM để thấy.";
        m["Free agent"] = "Cầu thủ tự do";
        m["the club's country"] = "quốc gia của CLB";
        m["First team"] = "Đội một";
        m["Reserves"] = "Đội dự bị";
        m["Team {0}"] = "Đội {0}";
        m["Youth {0}"] = "Đội trẻ {0}";
        m["Move now"] = "Chuyển ngay";
        m["Buying club"] = "CLB mua";
        m["Selling club"] = "CLB bán";
        m["Borrowing club"] = "CLB mượn";
        m["Choose destination club"] = "Chọn CLB đích";
        m["Choose destination club…"] = "Chọn CLB đích…";
        m["Confirm transfer"] = "Xác nhận chuyển nhượng";
        m["Confirm loan"] = "Xác nhận cho mượn";
        m["Confirm swap"] = "Xác nhận trao đổi";
        m["Make permanent"] = "Mua đứt";
        m["Recall to {0}"] = "Gọi lại về {0}";
        m["Recalled {0} to {1}. Continue in FM to see updates."] = "Đã gọi lại {0} về {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Release"] = "Giải phóng hợp đồng";
        m["Release Player"] = "Giải phóng cầu thủ";
        m["Released {0}. Continue in FM to see updates."] = "Đã giải phóng {0}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Undid release for {0}. Continue in FM to see updates."] = "Đã hoàn tác giải phóng cho {0}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Moved to {0} with a {1}% sell-on to {2}. Club finances show after Continue in FM."] =
            "Đã chuyển đến {0} kèm điều khoản {1}% bán lại cho {2}. Tài chính CLB cập nhật sau khi nhấn Continue trong FM.";
        m["Moved to {0}, but the sell-on was not added: {1}"] = "Đã chuyển đến {0}, nhưng chưa thêm được % bán lại: {1}";
        m["Moved to {0}. Set the new contract terms; club finances show after Continue in FM."] =
            "Đã chuyển đến {0}. Hãy đặt điều khoản hợp đồng mới; tài chính CLB cập nhật sau khi nhấn Continue trong FM.";
        m["Moved {0} to {1}. Continue in FM to see updates."] = "Đã chuyển {0} đến {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Saved {0}'s loan. Continue in FM to see updates."] = "Đã lưu hợp đồng cho mượn của {0}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Loaned to {0}. Continue in FM to see it."] = "Đã cho {0} mượn. Nhấn Continue trong FM để thấy.";
        m["Loan stopped: {0}"] = "Đã dừng cho mượn: {0}";
        m["Transfer stopped: {0}"] = "Đã dừng chuyển nhượng: {0}";
        m["Move now supports only the Sell-on percentage. Untick Move now to use the other terms."] =
            "\"Chuyển ngay\" chỉ hỗ trợ % bán lại. Bỏ chọn \"Chuyển ngay\" để dùng các điều khoản khác.";
        m["The current club can't be found; reload the save"] = "Không tìm thấy CLB hiện tại; hãy tải lại file save";
        m["This player's ID can't be read, so no sell-on can be added"] = "Không thể đọc ID cầu thủ nên không thể thêm % bán lại";
        m["This club has more than one team. Choose the team the player should join, then try again."] =
            "CLB này có nhiều hơn một đội. Hãy chọn đội cầu thủ sẽ gia nhập rồi thử lại.";
        m["Choose the club and team the player should join, then select the confirm button again."] =
            "Hãy chọn CLB và đội bóng mà cầu thủ sẽ gia nhập, rồi nhấn xác nhận lại.";
        m["Where should the player go?"] = "Cầu thủ sẽ chuyển đến đâu?";
        m["Which team?"] = "Chọn đội nào?";
        m["No teams available for this club."] = "CLB này không có đội nào khả dụng.";
        m["The new contract copies the one of the first player in the squad; change its terms afterwards on the Contract page. The contract and a new squad list are allocated in the game's heap; keep a backup of the save."] =
            "Hợp đồng mới sao chép từ cầu thủ đầu tiên trong đội; bạn có thể sửa điều khoản sau ở tab Hợp đồng. Hợp đồng và danh sách đội mới được cấp phát trong bộ nhớ heap của game; hãy sao lưu file save.";
        m["The new contract copies the one of the first player in the squad; change its terms afterwards on the Contract page. The contract are allocated in the game's heap; keep a backup of the save."] =
            "Hợp đồng mới sao chép từ cầu thủ đầu tiên trong đội; bạn có thể sửa điều khoản sau ở tab Hợp đồng. Hợp đồng được cấp phát trong bộ nhớ heap của game; hãy sao lưu file save.";
        m["The offer and its contract are allocated in the game's own heap; keep a backup of the save."] =
            "Đề nghị chuyển nhượng và hợp đồng được cấp phát trong heap của game; hãy sao lưu file save.";
        m["The sell-on record will be allocated outside the game's allocator; keep a backup of the save."] =
            "Bản ghi % bán lại sẽ được cấp phát ngoài bộ cấp phát của game; hãy sao lưu file save.";
        m["The squad is full, so a new squad list and the sell-on record will be allocated outside the game's allocator; keep a backup of the save."] =
            "Đội hình đã đầy nên danh sách đội mới và bản ghi % bán lại sẽ được cấp phát ngoài bộ cấp phát của game; hãy sao lưu file save.";
        m["The squad is full, so a new squad list will be allocated outside the game's allocator; keep a backup of the save."] =
            "Đội hình đã đầy nên danh sách đội mới sẽ được cấp phát ngoài bộ cấp phát của game; hãy sao lưu file save.";
        m["Transfer Fee: enter a fee of 0 or more."] = "Phí chuyển nhượng: nhập từ 0 trở lên.";
        m["Transfer Proposals: enter a fee of 0 or more"] = "Đề nghị chuyển nhượng: nhập phí từ 0 trở lên";
        m["Transfer · {0} → {1} · {2}"] = "Chuyển nhượng · {0} → {1} · {2}";
        m["Saved future transfer. Continue in FM to see updates."] = "Đã lưu chuyển nhượng tương lai. Nhấn Continue trong FM để thấy cập nhật.";
        m["Saved transfer fee. Continue in FM to see updates."] = "Đã lưu phí chuyển nhượng. Nhấn Continue trong FM để thấy cập nhật.";
        m["Saved transfer proposals. Continue in FM to see updates."] = "Đã lưu đề nghị chuyển nhượng. Nhấn Continue trong FM để thấy cập nhật.";
        m["Future transfer to {0} on {1} created. Continue in FM to see it."] =
            "Đã tạo chuyển nhượng tương lai đến {0} vào ngày {1}. Nhấn Continue trong FM để thấy.";
        m["Future Transfer: enter a date like {0} and a fee of 0 or more"] =
            "Chuyển nhượng tương lai: nhập ngày dạng {0} và phí từ 0 trở lên";
        m["Future transfer stopped: {0}"] = "Đã dừng chuyển nhượng tương lai: {0}";
        m["Remove future transfer"] = "Xóa chuyển nhượng tương lai";
        m["Removed the future transfer to {0}. Continue in FM to see it."] =
            "Đã xóa chuyển nhượng tương lai đến {0}. Nhấn Continue trong FM để thấy.";
        m["Remove stopped: {0}"] = "Đã dừng xóa: {0}";
        m["Accept Offer"] = "Chấp nhận đề nghị";
        m["Offer accepted"] = "Đã chấp nhận đề nghị";
        m["Offer accepted. Continue in FM to see updates."] = "Đã chấp nhận đề nghị. Nhấn Continue trong FM để thấy cập nhật.";
        m["Loading transfer proposals..."] = "Đang tải đề nghị chuyển nhượng...";
        m["No active transfer proposals."] = "Không có đề nghị chuyển nhượng nào đang hoạt động.";
        m["Transfer proposals could not be read: {0}"] = "Không đọc được đề nghị chuyển nhượng: {0}";
        m["Rejected"] = "Đã từ chối";
        m["Swap"] = "Trao đổi";
        m["Swap Player"] = "Trao đổi cầu thủ";
        m["Choose player to swap with"] = "Chọn cầu thủ để trao đổi";
        m["Swap stopped: {0}"] = "Đã dừng trao đổi: {0}";
        m["Swapped {0} and {1}. Club finances show after Continue in FM."] =
            "Đã trao đổi {0} và {1}. Tài chính CLB cập nhật sau khi nhấn Continue trong FM.";
        m["{0}: {1} → {2}\n{3}: {2} → {1}\nNo fee. Each goes to the team the other plays for."] =
            "{0}: {1} → {2}\n{3}: {2} → {1}\nKhông phí chuyển nhượng. Mỗi người tới đội mà người kia đang thi đấu.";
        m["Swap Call-Up"] = "Đổi suất triệu tập";
        m["Swap National Call Up"] = "Đổi suất triệu tập ĐTQG";
        m["Reading call-ups…"] = "Đang đọc danh sách triệu tập…";
        m["Swapped national call-ups of {0} and {1}. Continue in FM to see updates."] =
            "Đã đổi suất triệu tập ĐTQG giữa {0} và {1}. Nhấn Continue trong FM để thấy cập nhật.";
        m["{0}\n{1}  →  Free agent\nSquads: {2}   Captaincies: {3}"] =
            "{0}\n{1}  →  Cầu thủ tự do\nĐội hình: {2}   Đội trưởng: {3}";
        m["Clone"] = "Nhân bản";
        m["Players to Replace"] = "Cầu thủ được ghi đè";
        m["What to Copy"] = "Nội dung cần sao chép";
        m["Choose player to clone into"] = "Chọn cầu thủ để sao chép vào";
        m["Cloned onto {0}, {1} failed ({2})."] = "Đã sao chép sang {0}, {1} thất bại ({2}).";
        m["Cloned {0} onto {1} player(s), {2} field(s). Continue in FM to see updates."] =
            "Đã sao chép {0} sang {1} cầu thủ, {2} mục. Nhấn Continue trong FM để thấy cập nhật.";
        m["Compare Player"] = "So sánh cầu thủ";
        m["Compare with"] = "So sánh với";

        // Injuries, Languages, Preferred Moves, Promises, Relationships, Unhappiness, Career Plans, Work Permit, Unavailability
        m["Add injury"] = "Thêm chấn thương";
        m["Remove injury"] = "Xóa chấn thương";
        m["Clear current injuries"] = "Xóa chấn thương hiện tại";
        m["Remove every injury"] = "Xóa toàn bộ chấn thương";
        m["Contagious"] = "Dễ lây";
        m["Injury: Minimum Days is 1 or more, Extra Days 0 or more"] =
            "Chấn thương: Số ngày tối thiểu từ 1 trở lên, Số ngày thêm từ 0 trở lên";
        m["Injury: no injury to build a new one from was found yet"] = "Chấn thương: chưa tìm thấy mẫu chấn thương nào để tạo mới";
        m["Injury: pick a type for the new injury"] = "Chấn thương: hãy chọn một loại chấn thương mới";
        m["The injury type is no longer readable"] = "Loại chấn thương không còn đọc được nữa";
        m["Saved injury. Continue in FM to see updates."] = "Đã lưu chấn thương. Nhấn Continue trong FM để thấy cập nhật.";
        m["Languages"] = "Ngôn ngữ";
        m["Add language"] = "Thêm ngôn ngữ";
        m["Save the language edits first"] = "Hãy lưu chỉnh sửa ngôn ngữ trước";
        m["Languages: enter a value 1–10"] = "Ngôn ngữ: nhập giá trị từ 1–10";
        m["Saved languages. Continue in FM to see updates."] = "Đã lưu ngôn ngữ. Nhấn Continue trong FM để thấy cập nhật.";
        m["Preferred Moves"] = "Thói quen ưa thích";
        m["Saved preferred moves. Continue in FM to see updates."] = "Đã lưu tuyệt kỹ cá nhân. Nhấn Continue trong FM để thấy cập nhật.";
        m["Promise"] = "Lời hứa";
        m["Add promise"] = "Thêm lời hứa";
        m["Save the promise edits first"] = "Hãy lưu chỉnh sửa lời hứa trước";
        m["Promises: enter dates like {0}"] = "Lời hứa: nhập ngày dạng {0}";
        m["Saved promises. Continue in FM to see updates."] = "Đã lưu lời hứa. Nhấn Continue trong FM để thấy cập nhật.";
        m["Added a promise. Continue in FM to see updates."] = "Đã thêm lời hứa. Nhấn Continue trong FM để thấy cập nhật.";
        m["Removed the promise. Continue in FM to see updates."] = "Đã xóa lời hứa. Nhấn Continue trong FM để thấy cập nhật.";
        m["Unknown promise type"] = "Loại lời hứa không xác định";
        m["A promise needs a manager and a club"] = "Lời hứa cần có HLV trưởng và CLB";
        m["{0} has no club manager to make the promise"] = "{0} chưa có HLV trưởng CLB để lập lời hứa";
        m["Be appointed vice-captain"] = "Được bổ nhiệm làm đội phó";
        m["Do not fine this player"] = "Không phạt tiền cầu thủ này";
        m["Career Plans"] = "Kế hoạch sự nghiệp";
        m["Career Plans: pick an item for every row"] = "Kế hoạch sự nghiệp: chọn một mục cho mỗi dòng";
        m["Saved career plans. Continue in FM to see updates."] = "Đã lưu kế hoạch sự nghiệp. Nhấn Continue trong FM để thấy cập nhật.";
        m["Relationships"] = "Mối quan hệ";
        m["Add a relation"] = "Thêm mối quan hệ";
        m["Permanent"] = "Vĩnh viễn";
        m["Saved relationships. Continue in FM to see updates."] = "Đã lưu mối quan hệ. Nhấn Continue trong FM để thấy cập nhật.";
        m["Relationships: choose a type for every new row"] = "Mối quan hệ: chọn loại cho mọi dòng mới";
        m["Relationships: enter a value 1–100"] = "Mối quan hệ: nhập giá trị 1–100";
        m["Reason"] = "Lý do";
        m["Unhappiness"] = "Sự bất mãn";
        m["Unh"] = "BM";
        m["Inf"] = "TT";
        m["Inj"] = "CT";
        m["Frz"] = "ĐB";
        m["Tec."] = "KT";
        m["Fitness"] = "Thể lực";
        m["Sharpness"] = "Cảm giác bóng";
        m["Jadedness"] = "Độ mệt mỏi";
        m["Add a motive"] = "Thêm nguyên nhân";
        m["Remove unhappiness"] = "Xóa bất mãn";
        m["Unhappiness added"] = "Đã thêm bất mãn";
        m["Unhappiness removed"] = "Đã xóa bất mãn";
        m["Unhappiness saved"] = "Đã lưu bất mãn";
        m["Ban"] = "Án treo giò";
        m["{0} match ban"] = "Cấm thi đấu {0} trận";
        m["Personal Details"] = "Thông tin cá nhân";
        m["Work Permit"] = "Giấy phép lao động";
        m["Work Permit Denied"] = "Giấy phép lao động bị từ chối";
        m["Choose the nation that issues the permit"] = "Chọn quốc gia cấp giấy phép";
        m["Days in Country: enter 0–36500"] = "Số ngày tại quốc gia: nhập 0–36500";
        m["Work permit: enter a year from 1901 to {0}"] = "Giấy phép lao động: nhập năm từ 1901 đến {0}";
        m["Work permit: {0}"] = "Giấy phép lao động: {0}";
        m["Work permit ({0}): {1} granted{2}{3}. Continue in FM to see updates."] =
            "Giấy phép lao động ({0}): đã cấp {1}{2}{3}. Nhấn Continue trong FM để thấy cập nhật.";
        m["The work permits are still loading"] = "Giấy phép lao động vẫn đang tải";
        m["Saved work permit changes. Continue in FM to see updates."] =
            "Đã lưu thay đổi giấy phép lao động. Nhấn Continue trong FM để thấy cập nhật.";
        m["Other Nation Caps: Caps and Goals are 0–127"] = "Độ tuổi/ĐTQG khác: Số trận và Bàn thắng từ 0–127";
        m["Add unavailability"] = "Thêm thời gian vắng mặt";
        m["Saved unavailability."] = "Đã lưu thời gian vắng mặt.";
        m["Unavailability: enter whole numbers, a date like {0}, 1 day or more, 0–127 matches and a fulfilled count within that total"] =
            "Thời gian vắng mặt: nhập số nguyên, ngày dạng {0}, từ 1 ngày trở lên, 0–127 trận và số trận đã nghỉ không vượt quá tổng số";
        m["{0} has no club contract"] = "{0} không có hợp đồng CLB";
        m["{0} has no national contract"] = "{0} không có hợp đồng ĐTQG";
        m["Saved information. Continue in FM to see updates."] = "Đã lưu thông tin. Nhấn Continue trong FM để thấy cập nhật.";
        m["Some changes may have been saved. {0}"] = "Một số thay đổi có thể đã được lưu. {0}";
        m["Some information changes may have been saved. {0}"] = "Một số thay đổi thông tin có thể đã được lưu. {0}";
        m["Failed to save {0}: {1}"] = "Lưu {0} thất bại: {1}";
        m["Failed {0}: {1}"] = "{0} thất bại: {1}";
        m["Saved {0} change(s), then: {1}"] = "Đã lưu {0} thay đổi, sau đó: {1}";
        m["Saved {0}'s {1}. Continue in FM to see updates."] = "Đã lưu {1} của {0}. Nhấn Continue trong FM để thấy cập nhật.";
        m["Added {0}"] = "Đã thêm {0}";
        m["Added {0}. Its record lives outside the game's allocator; keep a backup of the save."] =
            "Đã thêm {0}. Bản ghi nằm ngoài bộ cấp phát của game; hãy sao lưu file save.";
        m["Added {0}. Its record lives outside the game's allocator; keep a backup of the save. Continue in FM to see updates."] =
            "Đã thêm {0}. Bản ghi nằm ngoài bộ cấp phát của game; hãy sao lưu file save. Nhấn Continue trong FM để thấy cập nhật.";
        m["Removed {0}"] = "Đã xóa {0}";
        m["Removed {0}."] = "Đã xóa {0}.";
        m["Removed {0}. Continue in FM to see updates."] = "Đã xóa {0}. Nhấn Continue trong FM để thấy cập nhật.";

        // Mass Edit & Review
        m["Mass Edit"] = "Chỉnh sửa hàng loạt";
        m["Review mass edit"] = "Xem lại chỉnh sửa hàng loạt";
        m["Review changes"] = "Xem lại thay đổi";
        m["Apply to"] = "Áp dụng cho";
        m["Choose who to edit..."] = "Chọn đối tượng chỉnh sửa...";
        m["Pick targets with Advanced Search; the result becomes the batch for the current scope."] =
            "Chọn mục tiêu bằng Tìm kiếm nâng cao; kết quả sẽ trở thành danh sách xử lý cho phạm vi hiện tại.";
        m["All on"] = "Bật hết";
        m["All off"] = "Tắt hết";
        m["Filled = don't change, empty = off, ticked = on"] = "Ô đặc = giữ nguyên, ô trống = tắt, có dấu tích = bật";
        m["Apply all"] = "Áp dụng tất cả";
        m["Apply {0}"] = "Áp dụng {0}";
        m["Discard {0}"] = "Bỏ qua {0}";
        m["Cancel"] = "Hủy";
        m["Don't change"] = "Không đổi";
        m["Set to"] = "Đặt thành";
        m["Set to {0:N0}"] = "Đặt thành {0:N0}";
        m["Change by"] = "Tăng / giảm thêm";
        m["Counting…"] = "Đang đếm…";
        m["No changes set"] = "Chưa thiết lập thay đổi nào";
        m["Nothing would change"] = "Không có gì thay đổi";
        m["{0:N0} change(s) set"] = "Đã đặt {0:N0} thay đổi";
        m["{0:N0} changes"] = "{0:N0} thay đổi";
        m["{0:N0} changes applied"] = "Đã áp dụng {0:N0} thay đổi";
        m["{0:N0} {1}, {2:N0} field(s), {3:N0} change(s) selected"] = "{0:N0} {1}, {2:N0} mục, {3:N0} thay đổi đã chọn";
        m["I have checked this table ({0:N0} changes)"] = "Tôi đã kiểm tra bảng này ({0:N0} thay đổi)";
        m["{0} table(s) not opened yet"] = "Còn {0} bảng chưa được mở xem";
        m["Cancelled: {0:N0} value(s) written. Continue in FM to see updates."] =
            "Đã hủy: đã ghi {0:N0} giá trị. Nhấn Continue trong FM để thấy cập nhật.";
        m["{0:N0} value(s) written. Continue in FM to see updates."] =
            "Đã ghi {0:N0} giá trị. Nhấn Continue trong FM để thấy cập nhật.";
        m["{0:N0} on, {1:N0} off"] = "{0:N0} bật, {1:N0} tắt";
        m["{0} must be between {1} and {2}"] = "{0} phải nằm trong khoảng {1} đến {2}";
        m["{0:N0} players (selected)"] = "{0:N0} cầu thủ (đã chọn)";
        m["(every target)"] = "(tất cả mục tiêu)";
        m["Field"] = "Trường";
        m["Before"] = "Trước";
        m["After"] = "Sau";
        m["Remove release clause"] = "Xóa điều khoản giải phóng";
        m["All-in-one (heal, inspire, bans, withdrawal, tactical familiarity)"] =
            "Tất cả trong một (hồi phục, truyền cảm hứng, xóa án phạt, hủy rút lui ĐTQG, hiểu biết chiến thuật)";
        m["Tactical understanding (familiarity and team cohesion to maximum)"] =
            "Hiểu biết chiến thuật (mức quen thuộc và gắn kết đội bóng tối đa)";
        m["players"] = "cầu thủ";
        m["coaches"] = "HLV";
        m["clubs"] = "CLB";
        m["nations"] = "quốc gia";
        m["Contract Type"] = "Loại hợp đồng";
        m["Expired"] = "Đã hết hạn";
        m["Expiring in 6 Months"] = "Hết hạn trong 6 tháng";
        m["Expiring in 1 Year"] = "Hết hạn trong 1 năm";
        m["Release clause"] = "Điều khoản giải phóng";
        m["Release clause fee"] = "Phí giải phóng";
        m["Technical/Gk"] = "Kỹ thuật / Thủ môn";
        m["Goal Keeper"] = "Thủ môn";
        m["Physical/Hidden"] = "Thể chất / Chỉ số ẩn";
        m["Mental Attributes"] = "Chỉ số tinh thần";
        m["Player Positions"] = "Vị trí cầu thủ";
        m["Coaching/Mental"] = "Huấn luyện / Tinh thần";
        m["Tactical/Non Tactical"] = "Chiến thuật / Khác";
        m["Coaching Attributes"] = "Chỉ số huấn luyện";
        m["Tactical Attributes"] = "Chỉ số chiến thuật";
        m["Non Tactical Attributes"] = "Chỉ số ngoài chiến thuật";
        m["Chairman Attributes"] = "Chỉ số Chủ tịch";
        m["Roles Attributes"] = "Chỉ số Vai trò";
        m["Overview"] = "Tổng quan";
        m["Others"] = "Khác";
        m["Status"] = "Trạng thái";

        // MassEditWindow Spaced(...) enum keys (exact casing from Spaced())
        m["Left Foot"] = "Chân trái";
        m["Right Foot"] = "Chân phải";
        m["Tendency To Punch"] = "Xu hướng đấm bóng";
        m["Coaching Attacking"] = "Huấn luyện tấn công";
        m["Coaching Defending"] = "Huấn luyện phòng ngự";
        m["Coaching Fitness"] = "Huấn luyện thể lực";
        m["Coaching Goalkeeping"] = "Huấn luyện Thủ môn";
        m["Coaching Possession"] = "Huấn luyện Kiểm soát bóng";
        m["Coaching Player"] = "Huấn luyện Cầu thủ";
        m["Coaching Tactical"] = "Huấn luyện chiến thuật";
        m["Coaching Technical"] = "Huấn luyện kỹ thuật";
        m["People Management"] = "Quản lý nhân sự";
        m["Working With Youngsters"] = "Làm việc với cầu thủ trẻ";
        m["Dirtiness Allowance"] = "Mức chấp nhận chơi tiểu xảo";
        m["Set Pieces"] = "Tình huống cố định";
        m["Tactical Attacking"] = "Tấn công (Chiến thuật)";
        m["Depth"] = "Độ sâu đội hình";
        m["Directness"] = "Sự trực diện";
        m["Flamboyancy"] = "Lối chơi hoa mỹ";
        m["Flexibility"] = "Sự linh hoạt";
        m["Free Roles"] = "Vai trò tự do";
        m["Offside"] = "Bẫy việt vị";
        m["Pressing"] = "Gây áp lực (Pressing)";
        m["Sitting Back"] = "Lùi sâu phòng ngự";
        m["Tempo"] = "Nhịp độ";
        m["Use Of Playmaker"] = "Sử dụng cầu thủ kiến thiết";
        m["Use Of Substitutions"] = "Sử dụng thay người";
        m["Width"] = "Độ rộng đội hình";
        m["Buying Players"] = "Mua cầu thủ";
        m["Hardness Of Training"] = "Độ khắc nghiệt tập luyện";
        m["Mind Games"] = "Đòn tâm lý";
        m["Squad Rotation"] = "Xoay tua đội hình";
        m["Judging Player Ability"] = "Đánh giá khả năng cầu thủ";
        m["Judging Player Potential"] = "Đánh giá tiềm năng cầu thủ";
        m["Judging Staff Ability"] = "Đánh giá năng lực nhân viên";
        m["Negotiating"] = "Đàm phán";
        m["Authority"] = "Uy quyền";
        m["Motivating"] = "Khả năng tạo động lực";
        m["Tactical Knowledge"] = "Kiến thức chiến thuật";
        m["Physiotherapy"] = "Vật lý trị liệu";
        m["Sports Science"] = "Khoa học thể thao";
        m["Judging Player Data"] = "Phân tích dữ liệu cầu thủ";
        m["Judging Team Data"] = "Phân tích dữ liệu đội bóng";
        m["Presenting Data"] = "Trình bày dữ liệu";
        m["Business"] = "Kinh doanh";
        m["Interference"] = "Sự can thiệp";
        m["Resources"] = "Nguồn lực";
    }
}
