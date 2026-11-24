namespace Fm27Editor.App;

public static partial class Loc
{
    static partial void RegisterShell(Dictionary<string, string> m)
    {
        m["Universal search: player, club, stadium, award or ID…"] = "Tìm kiếm tổng hợp…";
        m["Injuries"] = "Chấn thương";
        m["Search injury..."] = "Tìm chấn thương...";

        // Top toolbar & chrome (AppShell.cs)
        m["Reload active tab (Ctrl+R)"] = "Tải lại tab hiện tại (Ctrl+R)";
        m["Search"] = "Tìm kiếm";
        m["Search anything — players, clubs, staff, nations, competitions, stadiums, awards, referees, cities"] =
            "Tìm bất kỳ thứ gì — cầu thủ, CLB, nhân viên, quốc gia, giải đấu, sân vận động, giải thưởng, trọng tài, thành phố";
        m["Advanced Search"] = "Tìm kiếm nâng cao";
        m["Mass Edit (Ctrl+M)"] = "Chỉnh sửa hàng loạt (Ctrl+M)";
        m["Edit History"] = "Lịch sử chỉnh sửa";
        m["Gameplay Tools (Wonderkid Academy & Development Speed)"] = "Công cụ trong game (Học viện Thần đồng & Tốc độ phát triển)";
        m["Your Manager"] = "HLV của bạn";
        m["No player-controlled manager is currently managing a team."] = "Hiện không có HLV người chơi nào đang dẫn đội.";
        m["Squad Rules"] = "Quy định đội hình";
        m["Squad Registration Anytime"] = "Đăng ký đội hình bất cứ lúc nào";
        m["Squad Numbers Anytime"] = "Đổi số áo bất cứ lúc nào";
        m["On"] = "Bật";
        m["Off"] = "Tắt";
        m["The club's manager changed since the squad rules were read."] = "CLB đã đổi HLV kể từ lúc đọc quy định đội hình.";
        m["Freezer (locked values)"] = "Đóng băng giá trị (các mục đang khóa)";
        m["Load Game"] = "Tải dữ liệu Game";
        m["View on GitHub — {0}"] = "Xem trên GitHub — {0}";
        m["Close current tab (Ctrl+W)"] = "Đóng tab hiện tại (Ctrl+W)";
        m["Toggle between Dark and Light mode"] = "Chuyển giao diện Tối / Sáng";
        m["No unsaved changes"] = "Không có thay đổi chưa lưu";
        m["Loading search index…"] = "Đang tải danh mục tìm kiếm…";
        m["Reading search lists… ({0} / 8)"] = "Đang đọc danh sách tìm kiếm… ({0} / 8)";
        m["{0} · Free agent"] = "{0} · Cầu thủ tự do";
        m["Rep {0}"] = "Danh tiếng {0}";
        m["Player data changed since it was read; press Load Game to reload."] =
            "Dữ liệu cầu thủ đã thay đổi từ lúc đọc; nhấn Tải dữ liệu Game để đọc lại.";
        m["Freezer unavailable: {0}"] = "Đóng băng không khả dụng: {0}";
        m["Freezer stopped: {0}"] = "Đóng băng đã dừng: {0}";
        m["Club screen unavailable"] = "Màn hình CLB không khả dụng";
        m["Nation screen unavailable"] = "Màn hình Quốc gia không khả dụng";
        m["Competition screen unavailable"] = "Màn hình Giải đấu không khả dụng";
        m["Save Changes"] = "Lưu thay đổi";
        m["Save {0}"] = "Lưu {0}";
        m["Save contract"] = "Lưu hợp đồng";
        m["Save loan"] = "Lưu hợp đồng mượn";

        // Unified Search overlay
        m["Type to search players, clubs, nations, competitions, staff, stadiums…"] =
            "Nhập để tìm cầu thủ, CLB, quốc gia, giải đấu, nhân viên, sân vận động…";
        m["Close"] = "Đóng";
        m["All categories"] = "Tất cả danh mục";
        m["Only {0}"] = "Chỉ {0}";
        m["Results"] = "Kết quả";
        m["{0:N0} result(s) for \"{1}\""] = "{0:N0} kết quả cho \"{1}\"";
        m["{0:N0} in {1}"] = "{0:N0} trong {1}";
        m["Type at least 1 character to search across the loaded game"] =
            "Nhập ít nhất 1 ký tự để tìm trên toàn bộ dữ liệu game đang mở";
        m["No matches for \"{0}\""] = "Không tìm thấy kết quả cho \"{0}\"";
        m["Matches"] = "Kết quả khớp";
        m["Showing {0:N0} of {1:N0} matches — narrow with more characters or click All categories above"] =
            "Đang hiển thị {0:N0} / {1:N0} kết quả — nhập thêm ký tự hoặc bấm Tất cả danh mục ở trên";

        // Module bar & entity screen labels
        m["Staff"] = "Nhân sự";
        m["Clubs"] = "Câu lạc bộ";
        m["Nations"] = "Quốc gia";
        m["Stadiums"] = "Sân vận động";
        m["Competitions"] = "Giải đấu";
        m["Awards"] = "Giải thưởng";
        m["Referees"] = "Trọng tài";
        m["Cities"] = "Thành phố";
        m["Injury Types"] = "Loại chấn thương";

        // Kind pills / EntityScreen (EntityScreen.cs)
        m["Player"] = "Cầu thủ";
        m["Nation"] = "Quốc gia";
        m["Competition"] = "Giải đấu";
        m["Stadium"] = "Sân vận động";
        m["Award"] = "Giải thưởng";
        m["Referee"] = "Trọng tài";
        m["City"] = "Thành phố";
        m["Loading {0}…"] = "Đang tải {0}…";
        m["Reloaded {0}"] = "Đã tải lại {0}";
        m["{0:N0} {1}"] = "{0:N0} {1}";
        m["{0:N0} of {1:N0} {2}"] = "{0:N0} / {1:N0} {2}";
        m["{0:N0} of {1:N0} {2} shown ({3:N0} hidden by filter)"] = "{0:N0} / {1:N0} {2} hiển thị ({3:N0} bị ẩn bởi bộ lọc)";
        m["{0}: no longer readable"] = "{0}: không còn đọc được";
        m["{0} not loaded yet"] = "{0} chưa được tải";
        m["Saved {0}"] = "Đã lưu {0}";
        m["{0} updated, saved {1}"] = "{0} đã cập nhật, lưu {1}";
        m["Saved. Continue in FM to see updates."] = "Đã lưu. Nhấn Continue trong FM để thấy thay đổi.";
        m["Changes saved. Continue in FM to see updates."] = "Đã lưu thay đổi. Nhấn Continue trong FM để thấy cập nhật.";
        m["Saved {0} change(s). Continue in FM to see updates."] = "Đã lưu {0} thay đổi. Nhấn Continue trong FM để thấy cập nhật.";
        m["Some changes may have been saved. Failed to save {0}: {1}"] = "Một số thay đổi có thể đã được lưu. Lưu {0} thất bại: {1}";
        m["{0} saved. {1} Review remaining changes."] = "Đã lưu {0}. {1} Kiểm tra lại các thay đổi còn lại.";
        m["{0} saved. {1}"] = "Đã lưu {0}. {1}";
        m["{0} unsaved change(s)"] = "{0} thay đổi chưa lưu";

        // MainWindow filters, columns, sorting, presets, loading
        m["Search player name or ID..."] = "Tìm tên hoặc ID cầu thủ...";
        m["Reset"] = "Đặt lại";
        m["Clear"] = "Xóa";
        m["Men"] = "Nam";
        m["Women"] = "Nữ";
        m["All"] = "Tất cả";
        m["Gender filter"] = "Lọc theo giới tính";
        m["Favourites"] = "Yêu thích";
        m["All Clubs"] = "Tất cả CLB";
        m["All Nationalities"] = "Tất cả quốc tịch";
        m["All Leagues"] = "Tất cả giải đấu";
        m["Preset: None"] = "Bộ lọc mẫu: Không";
        m["Wonderkids (age <= 20, PA > 150)"] = "Thần đồng (tuổi <= 20, TN > 150)";
        m["Fast Striker"] = "Tiền đạo tốc độ";
        m["Playmaker"] = "Nhạc trưởng kiến thiết";
        m["Ball-Playing Defender"] = "Trung vệ chơi chân";
        m["Sweeper Keeper"] = "Thủ môn quét";
        m["Asking Price"] = "Giá yêu cầu";
        m["Sort: CA"] = "Sắp xếp: NLHT";
        m["Sort: PA"] = "Sắp xếp: TN";
        m["Sort: Age"] = "Sắp xếp: Tuổi";
        m["Sort: Name"] = "Sắp xếp: Tên";
        m["Sort: Club"] = "Sắp xếp: CLB";
        m["Sort: Nationality"] = "Sắp xếp: Quốc tịch";
        m["Sort: League"] = "Sắp xếp: Giải đấu";
        m["Attr: {0}"] = "Chỉ số: {0}";
        m["League: {0}"] = "Giải: {0}";
        m["Club: {0}"] = "CLB: {0}";
        m["Team: {0}"] = "Đội: {0}";
        m["Nation: {0}"] = "Quốc tịch: {0}";
        m["Free Player"] = "Cầu thủ tự do";
        m["Expired"] = "Đã hết hạn";
        m["Expiring in 6 Months"] = "Hết hạn trong 6 tháng";
        m["Expiring in 1 Year"] = "Hết hạn trong 1 năm";
        m["Preferred Moves"] = "Thói quen ưa thích";
        m["Pos: {0} {1}–{2}"] = "Vị trí: {0} {1}–{2}";
        m["Pos: {0} >= {1}"] = "Vị trí: {0} >= {1}";
        m["filter"] = "bộ lọc";
        m["{0} filter"] = "bộ lọc {0}";
        m["{0:N0} of {1:N0} {2} shown ({3:N0} hidden by {4})"] = "{0:N0} / {1:N0} {2} hiển thị ({3:N0} bị ẩn bởi {4})";
        m["0 players"] = "0 cầu thủ";
        m["{0} players"] = "{0} cầu thủ";
        m["1 player"] = "1 cầu thủ";
        m["{0} yrs"] = "{0} tuổi";
        m["Player no longer available; reloading"] = "Cầu thủ không còn khả dụng; đang tải lại";
        m["Connecting to the game…"] = "Đang kết nối tới game…";
        m["Connecting…"] = "Đang kết nối…";
        m["Reading players…"] = "Đang đọc cầu thủ…";
        m["Reading clubs and nations…"] = "Đang đọc CLB và quốc gia…";
        m["Reading search lists…"] = "Đang đọc danh sách tìm kiếm…";
        m["Reading career plans…"] = "Đang đọc kế hoạch sự nghiệp…";
        m["Reading…"] = "Đang đọc…";
        m["Loading game data"] = "Đang tải dữ liệu game";
        m["Loading game…"] = "Đang tải dữ liệu game…";

        // Player sections & pitch
        m["Information"] = "Thông tin";
        m["Positions"] = "Vị trí";
        m["Career Plans"] = "Kế hoạch sự nghiệp";
        m["Move to club"] = "Chuyển đến CLB";
        m["Swap Player"] = "Hoán đổi cầu thủ";
        m["Work Permit"] = "Giấy phép lao động";
        m["Promises"] = "Lời hứa";
        m["Unavailability"] = "Không thể thi đấu";
        m["Future Transfer"] = "Chuyển nhượng tương lai";
        m["Transfer Proposals"] = "Đề nghị chuyển nhượng";
        m["Injury"] = "Chấn thương";
        m["Languages"] = "Ngôn ngữ";
        m["Comparison"] = "So sánh";
        m["Clone Center"] = "Nhân bản cầu thủ";
        m["Swap National Call Up"] = "Hoán đổi triệu tập ĐTQG";
        m["Defensive"] = "Phòng ngự";
        m["Midfield & Attack"] = "Tiền vệ & Tấn công";
        m["Rating"] = "Đánh giá";
        m["Ability"] = "Năng lực";
        m["Reputation"] = "Danh tiếng";
        m["Body"] = "Thể chất";
        m["Personality"] = "Tính cách";
        m["Technical"] = "Kỹ thuật";
        m["Mental"] = "Tinh thần";
        m["Physical"] = "Thể chất";
        m["Goalkeeping"] = "Thủ môn";
        m["Hidden"] = "Chỉ số ẩn";
        m["Home Reputation"] = "Danh tiếng trong nước";
        m["Current Reputation"] = "Danh tiếng hiện tại";
        m["World Reputation"] = "Danh tiếng thế giới";
        m["Height"] = "Chiều cao";
        m["Born"] = "Ngày sinh";
        m["Date of Birth"] = "Ngày sinh";

        // Full positions (PitchLayout & PlayerFields)
        m["Goalkeeper"] = "Thủ môn";
        m["Defender (Right)"] = "Hậu vệ (Phải)";
        m["Defender (Centre)"] = "Trung vệ (Giữa)";
        m["Defender (Left)"] = "Hậu vệ (Trái)";
        m["Wing-Back (Right)"] = "Hậu vệ cánh (Phải)";
        m["Defensive Midfielder"] = "Tiền vệ phòng ngự";
        m["Wing-Back (Left)"] = "Hậu vệ cánh (Trái)";
        m["Midfielder (Right)"] = "Tiền vệ (Phải)";
        m["Midfielder (Centre)"] = "Tiền vệ (Trung tâm)";
        m["Midfielder (Left)"] = "Tiền vệ (Trái)";
        m["Attacking Midfielder (Right)"] = "Tiền vệ tấn công (Phải)";
        m["Attacking Midfielder (Centre)"] = "Tiền vệ tấn công (Trung tâm)";
        m["Attacking Midfielder (Left)"] = "Tiền vệ tấn công (Trái)";
        m["Striker"] = "Tiền đạo cắm";
        m["Defender Left"] = "Hậu vệ trái";
        m["Defender Central"] = "Trung vệ";
        m["Defender Right"] = "Hậu vệ phải";
        m["Wing Back Left"] = "Hậu vệ cánh trái";
        m["Wing Back Right"] = "Hậu vệ cánh phải";
        m["Midfielder Left"] = "Tiền vệ trái";
        m["Midfielder Central"] = "Tiền vệ trung tâm";
        m["Midfielder Right"] = "Tiền vệ phải";
        m["Attacking Mid Left"] = "Tiền vệ tấn công trái";
        m["Attacking Mid Central"] = "Tiền vệ tấn công trung tâm";
        m["Attacking Mid Right"] = "Tiền vệ tấn công phải";

        // Dialogs & Edit History & Favorites & SaveWatcher & UserMessage
        m["OK"] = "Đồng ý";
        m["Confirm"] = "Xác nhận";
        m["Save"] = "Lưu";
        m["Discard"] = "Bỏ qua";
        m["Remove"] = "Gỡ bỏ";
        m["Search changes"] = "Tìm thay đổi";
        m["Clear history"] = "Xóa lịch sử";
        m["Undo all"] = "Hoàn tác tất cả";
        m["Undone"] = "Đã hoàn tác";
        m["Undo"] = "Hoàn tác";
        m["Nothing to undo"] = "Không có gì để hoàn tác";
        m["Undo all {0:N0} change(s) in the history?"] = "Hoàn tác toàn bộ {0:N0} thay đổi trong lịch sử?";
        m["Undo all: {0:N0} restored"] = "Hoàn tác tất cả: đã khôi phục {0:N0}";
        m["Undo all: {0:N0} restored, {1:N0} refused (the game changed them since)"] =
            "Hoàn tác tất cả: khôi phục {0:N0}, từ chối {1:N0} (game đã thay đổi giá trị sau đó)";
        m["{0:N0} change(s) in {1} save(s)"] = "{0:N0} thay đổi trong {1} lần lưu";
        m["{0} · {1:N0} change(s)"] = "{0} · {1:N0} thay đổi";
        m["Undo {0} ({1}): {2} restored"] = "Hoàn tác {0} ({1}): đã khôi phục {2}";
        m["Undo {0} ({1}): {2} restored, {3} refused ({4})"] = "Hoàn tác {0} ({1}): khôi phục {2}, từ chối {3} ({4})";
        m["… {0:N0} more; Undo all restores every one"] = "… còn {0:N0} mục nữa; Hoàn tác tất cả sẽ khôi phục toàn bộ";
        m["Restored {0} to {1}"] = "Đã khôi phục {0} về {1}";
        m["Cannot undo {0}: {1}"] = "Không thể hoàn tác {0}: {1}";
        m["Page save"] = "Lưu trang";
        m["Other"] = "Khác";
        m["Edits"] = "Chỉnh sửa";
        m["Edit"] = "Sửa";

        m["Type the name to search"] = "Nhập tên để tìm kiếm";
        m["Advanced Search Results - {0:N0} {1}"] = "Kết quả Tìm kiếm nâng cao - {0:N0} {1}";
        m["staff"] = "nhân sự";
        m["clubs"] = "CLB";
        m["{0:N0} of {1:N0} {2} shown ({3:N0} hidden{4})"] = "{0:N0} / {1:N0} {2} hiển thị ({3:N0} bị ẩn{4})";
        m["Job"] = "Chức vụ";
        m["ID"] = "ID";

        m["Unsaved changes"] = "Thay đổi chưa được lưu";
        m["This page has changes that are not saved yet. Save them, discard them, or stay on the tab."] =
            "Trang này có thay đổi chưa lưu. Bạn muốn lưu, bỏ qua hay ở lại tab này?";

        m["Report a problem"] = "Báo cáo lỗi";
        m["What happened? What were you doing?"] = "Điều gì đã xảy ra? Bạn đang thao tác gì?";
        m["Create report"] = "Tạo báo cáo";
        m["Open folder"] = "Mở thư mục";
        m["No log is available for this run."] = "Không có nhật ký cho phiên chạy này.";

        m["Update available"] = "Có bản cập nhật mới";
        m["Version {0} is out. Update now downloads it, closes the editor and opens the new version. Changes you have not saved yet are lost; anything running in the game (Development Speed, Wonderkid Academy) keeps running."] =
            "Phiên bản {0} đã ra mắt. Cập nhật ngay sẽ tải xuống, đóng trình chỉnh sửa và mở bản mới. Các thay đổi chưa lưu sẽ mất; các tính năng đang chạy trong game (Tốc độ phát triển, Học viện Thần đồng) vẫn giữ nguyên.";
        m["Later"] = "Để sau";
        m["Update now"] = "Cập nhật ngay";
        m["Downloading {0}…"] = "Đang tải {0}…";
        m["Update failed: {0}"] = "Cập nhật thất bại: {0}";
        m["Update failed"] = "Cập nhật thất bại";
        m["The update could not be installed ({0}). This version is unchanged. You can download {1} from GitHub instead."] =
            "Không thể cài bản cập nhật ({0}). Phiên bản hiện tại giữ nguyên. Bạn có thể tải {1} từ GitHub.";
        m["Open GitHub"] = "Mở GitHub";
        m["Version {0} is out on GitHub. Download the latest release from the release page."] =
            "Phiên bản {0} đã có trên GitHub. Hãy tải bản mới nhất từ trang phát hành.";
        m["Could not open the browser: {0}"] = "Không thể mở trình duyệt: {0}";

        m["No favourites yet. Star a player on his page."] = "Chưa có cầu thủ yêu thích. Hãy gắn sao cầu thủ ở trang chi tiết.";
        m["Player no longer readable"] = "Cầu thủ không còn đọc được";
        m["Nothing to reload here"] = "Không có gì để tải lại ở đây";

        m["The game has no save loaded"] = "Game chưa tải file save nào";
        m["A save was loaded in the game"] = "Một file save đã được tải trong game";
        m["Load Game to refresh this tab."] = "Bấm Load Game để tải lại tab này.";
        m["This item is unavailable in the loaded save."] = "Mục này không có trong save đang mở.";
        m["Move tab left"] = "Chuyển tab sang trái";
        m["Move tab right"] = "Chuyển tab sang phải";
        m["A different save was loaded in the game"] = "Một file save khác đã được tải trong game";
        m["Press Load Game to read the new save"] = "Nhấn Tải dữ liệu Game để đọc file save mới";
        m["No save is loaded in the game yet"] = "Chưa có file save nào được tải trong game";
        m["Save not loaded"] = "Chưa tải file save";

        // macOS plugin setup (MacPluginSetupView.cs)
        m["Set up the editor plugin"] = "Cài plugin của editor";
        m["With System Integrity Protection on, the editor reads Football Manager 27 from Steam through its plugin. Set up adds the plugin to the game's Steam launch options; the game's files are not changed. Quit Football Manager 27 and Steam before you select Set up."] =
            "Khi System Integrity Protection đang bật, editor đọc Football Manager 27 bản Steam qua plugin của mình. Cài đặt sẽ thêm plugin vào Launch Options của game trên Steam; file của game không bị thay đổi. Hãy thoát Football Manager 27 và Steam trước khi chọn Cài đặt.";
        m["Set up"] = "Cài đặt";
        m["Restart Football Manager 27"] = "Mở lại Football Manager 27";
        m["The plugin is set up, but this game was started without it. Quit Football Manager 27, start it again from Steam, load your save, then select Load Game."] =
            "Plugin đã được cài, nhưng game này được mở trước khi có plugin. Hãy thoát Football Manager 27, mở lại từ Steam, tải save rồi chọn Tải dữ liệu Game.";
        m["Quit Steam first"] = "Hãy thoát Steam trước";
        m["Steam rewrites its settings when it quits, which would undo the setup. Quit Football Manager 27 and Steam, then select Set up."] =
            "Steam ghi lại cài đặt khi thoát, việc đó sẽ xóa mất phần cài đặt. Hãy thoát Football Manager 27 và Steam rồi chọn Cài đặt.";
        m["Plugin set up"] = "Đã cài plugin";
        m["Start Steam and Football Manager 27, load your save, then select Load Game. If the game was open, quit it and start it again from Steam."] =
            "Mở Steam và Football Manager 27, tải save rồi chọn Tải dữ liệu Game. Nếu game đang mở, hãy thoát rồi mở lại từ Steam.";
        m["Plugin not set up"] = "Chưa cài được plugin";
        m["Steam is still running: it rewrites its settings when it quits"] = "Steam vẫn đang chạy. Hãy thoát Steam để ứng dụng không ghi đè cài đặt plugin.";
        m["The editor's plugin is missing from the app"] = "Không tìm thấy plugin trong gói cài đặt của trình chỉnh sửa.";
        m["Start Football Manager from Steam once, quit it, then set up again"] = "Mở Football Manager từ Steam một lần, sau đó thoát game và thử cài đặt lại.";
        m["Football Manager 27 is not in this Steam account's settings"] = "Không tìm thấy Football Manager 27 trong cài đặt của tài khoản Steam này.";
        m["Steam's settings file ends inside a string"] = "Tệp cài đặt Steam bị thiếu phần cuối của một chuỗi văn bản.";
        m["Steam's settings file has an unclosed block"] = "Tệp cài đặt Steam có một khối dữ liệu chưa được đóng.";
        m["This game version ({0}) is supported, but no save is loaded yet. Load your save in the game, save it without exiting, then press Load Game again."] =
            "Phiên bản game ({0}) được hỗ trợ, nhưng chưa có file save nào được tải. Hãy mở save trong game, lưu lại không cần thoát, rồi nhấn Tải dữ liệu Game.";
        m["Development speed: removed {0} stale player row(s)."] = "Tốc độ phát triển: đã gỡ {0} cầu thủ cũ không còn hiệu lực.";
        m["Development speed cleanup failed: {0}"] = "Dọn dẹp tốc độ phát triển thất bại: {0}";
        m["Force Board Request switched off"] = "Đã tắt Buộc duyệt yêu cầu ban lãnh đạo";
        m["A save was loaded, so Force Board Request was switched off. You can turn it on again in Gameplay Tools."] = "Đã nạp bản lưu nên tính năng Buộc duyệt yêu cầu ban lãnh đạo đã tắt. Bạn có thể bật lại trong Công cụ trong game.";
        m["Wonderkid Academy switched off"] = "Học viện Thần đồng đã tắt";
        m["A save was loaded, so Wonderkid Academy was switched off. Your settings are kept. You can turn it on again in Gameplay Tools."] =
            "Một save vừa được tải, nên Học viện Thần đồng đã tự tắt. Cài đặt của bạn vẫn được giữ: hãy bật lại trong Công cụ trong game nếu muốn dùng cho save này.";
        m["Wonderkid Academy could not be switched off: {0}"] = "Không thể tắt Học viện Thần đồng: {0}";

        // Connection & errors
        m["Disconnected"] = "Chưa kết nối";
        m["Football Manager 27 (fm.exe) is not running"] = "Football Manager 27 (fm.exe) chưa chạy";
        m["game_plugin.dll is not loaded yet"] = "game_plugin.dll chưa được tải vào game";
        // macOS process discovery (MacProcessDiscovery)
        m["Football Manager 27 is not running"] = "Football Manager 27 chưa chạy";
        m["game_plugin is not loaded yet"] = "game_plugin chưa được tải vào game";
        m["A different save is loaded in the game. Press Load Game first."] = "Game đã tải một file save khác. Hãy nhấn Tải dữ liệu Game trước.";
        m["This game version is not supported"] = "Phiên bản game này không được hỗ trợ";
        m["Some clause fields may have been saved. Reload before retrying."] = "Một số trường điều khoản có thể đã được lưu. Hãy tải lại trước khi thử lại.";
        m["Player data changed in game; reloaded"] = "Dữ liệu cầu thủ trong game đã thay đổi; đã tải lại";
        m["Couldn't access game data. Check that a save is loaded and retry."] = "Không thể truy cập dữ liệu game. Hãy kiểm tra file save đã mở chưa rồi thử lại.";
        m["Value is outside the allowed range"] = "Giá trị nằm ngoài phạm vi cho phép";
        m["Windows error {0}: {1}"] = "Lỗi Windows {0}: {1}";
    }
}
