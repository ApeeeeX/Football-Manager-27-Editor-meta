<p align="center"><img src="docs/images/icon.png" alt="Football Manager 27 Editor" width="160"></p>

# Football Manager 27 Editor

[![Build](https://github.com/Jackhamerp24/Football-Manager-27-Editor/actions/workflows/build.yml/badge.svg)](https://github.com/Jackhamerp24/Football-Manager-27-Editor/actions/workflows/build.yml)

[English](README.md) | Tiếng Việt

Editor chỉnh save trực tiếp khi game đang chạy dành cho **Football Manager 27** và **Football Manager Touch** trên **Windows (Steam và Xbox / Game Pass)** và **macOS chip Apple (Steam và Touch)**. App đọc và ghi trực tiếp bộ nhớ tiến trình game (`fm.exe` / `game_plugin.dll` trên Windows; `Football Manager 27` hoặc `Football Manager Touch` / `game_plugin` trên macOS thông qua helper nhỏ cài một lần ở lần kết nối đầu) mà không sửa file cài đặt trên đĩa.

Chỉ dùng cho save chơi một mình, offline. Không dùng khi chơi online hay save chung. Hãy sao lưu save trước khi sửa; thay đổi vào RAM ngay lập tức và game sẽ vẽ lại màn hình sau khi bạn bấm **Continue**.

## Cơ chế hoạt động

Tải toàn bộ cầu thủ (khoảng 50.000 người) mất chưa tới 1 giây. Mọi thao tác sửa từ giao diện đi qua lịch sử hỗ trợ **Undo** và cơ chế ghi có kiểm tra:

- App từ chối gắn vào bản game chưa có profile offset, tránh ghi nhầm địa chỉ RAM.
- Trước khi ghi, app kiểm tra vtable và snapshot bộ nhớ vẫn khớp lúc đọc; sau khi ghi, app đọc lại để xác nhận và dừng ngay nếu có sai lệch.
- Các thao tác thêm dòng vào danh sách của game (ngôn ngữ, lời hứa, work permit, chuyển nhượng tương lai, tầm nhìn CLB, thoả thuận) cấp phát qua bộ cấp phát của chính game khi được hỗ trợ, để **Continue** và **Load Game** giải phóng sạch, không văng game.

## Nền tảng và bản game hỗ trợ

Mỗi bản game chính thức đều được kiểm trên game thật, đối chiếu save hiệu chuẩn, chạy round-trip ghi từng byte, kiểm tra heap của game và vượt cổng crash-gate (Continue + Load Game) trước khi mở khoá ghi.

| Nền tảng | Phiên bản | Bản build hỗ trợ | Ghi chú |
|---|---|---|---|
| **Windows 10/11 (x64)** | **Steam** | **`27.1.0+2505117`**, `27.0.1+2503222`, `27.0.1+2494687`, `27.0.0+2493426`, `27.0.0+2486695` | Đọc/ghi đầy đủ, hỗ trợ cầu thủ kiêm nhân sự, hook x64 |
| **Windows 10/11 (x64)** | **Xbox / Game Pass** | **`27.1.0+2505117`**, `27.0.1+2494687`, `27.0.0+2493426` | Tính năng tương đương Steam, hỗ trợ cầu thủ kiêm nhân sự |
| **macOS 13+ (Apple silicon)** | **FM27 Steam** | **`27.1.0+2505117`**, `27.0.1+2503222` | NativeAOT `.dmg`, hook ARM64, đã qua crash-gate Continue + Load Game |
| **macOS 13+ (Apple silicon)** | **FM27 Touch** | **`27.1.0+2505719`**, `27.0.1+2503222`, `27.0.0+2492889` *(thử nghiệm)* | NativeAOT `.dmg`, `27.1.0`/`27.0.1` đã qua crash-gate Continue + Load Game (Touch không có danh sách trọng tài) |

Bản build mới hơn cần profile offset riêng; xem [Khi game có patch](#khi-game-có-patch).

## Tải về

Tải ở [Releases](../../releases/latest):

| File | Nền tảng | Dùng để |
|---|---|---|
| **`Fm27Editor.exe`** | **Windows (x64)** | App editor desktop. Chạy độc lập ở mọi thư mục, không cần cài thêm .NET |
| **`Fm27Editor-mac-arm64.dmg`** | **macOS (chip Apple)** | App editor desktop, dùng chung cho FM27 Steam và FM27 Touch |
| `Fm27Editor.Tools.exe` | Windows (x64) | Bộ công cụ dòng lệnh (`players`, `set`, `verify`, `newbuild`, `verify-build`) |

Mở FM27 hoặc FM27 Touch và nạp save trước khi mở editor:

- **Windows:** chạy thẳng `Fm27Editor.exe` (không đòi quyền admin).
- **macOS:** mở file `.dmg` và kéo **FM27 Editor** vào `/Applications`. Do bản Mac chỉ ký ad-hoc chưa qua Apple notarisation, lần đầu mở sẽ bị chặn: vào **System Settings → Privacy & Security → Open Anyway** (macOS 13–14: chuột phải vào app → **Open**). Lần đầu gắn vào game, hệ thống hỏi mật khẩu một lần để cài `Fm27EditorHelper`.

### Tính toàn vẹn bản build và quyền hệ thống

Toàn bộ bản phát hành được build trực tiếp từ repo này bằng [Build workflow](.github/workflows/build.yml) trên GitHub Actions, không build thủ công trên máy cá nhân.

- So sánh hash với `SHA256SUMS.txt` (`Get-FileHash Fm27Editor.exe` trên Windows hoặc `shasum -a 256 Fm27Editor-mac-arm64.dmg` trên macOS).
- Kiểm chứng chứng thực build có chữ ký bằng [GitHub CLI](https://cli.github.com):

  ```bash
  gh attestation verify Fm27Editor.exe --repo Jackhamerp24/Football-Manager-27-Editor
  gh attestation verify Fm27Editor-mac-arm64.dmg --repo Jackhamerp24/Football-Manager-27-Editor
  ```

Yêu cầu mạng duy nhất của app là một lần kiểm tra khi mở xem GitHub có bản mới hơn không (không gửi thông tin gì, mất mạng tự bỏ qua). File ghi trên đĩa chỉ gồm cài đặt/log riêng (`%LOCALAPPDATA%\Fm27Editor` trên Windows; thư mục app support và `favorites.json` cạnh file chạy) cùng các file export hoặc zip báo lỗi do bạn chủ động tạo.

## App làm được gì

| Mục | Tính năng |
|---|---|
| Cầu thủ | Chỉ số, vị trí, phân loại tính cách và chỉ số ẩn, CA/PA, danh tiếng, chiều cao, ngày sinh, quốc tịch, nơi sinh và ngoại hình; hợp đồng hiện tại và tương lai, thưởng và điều khoản; kế hoạch sự nghiệp, quan hệ, ngôn ngữ, động tác ưa thích; chấn thương, án treo giò, bất mãn, triệu tập ĐTQG, giấy phép lao động và mọi loại lời hứa; Role Scores tích hợp sẵn; cầu thủ kiêm nhân sự |
| Chuyển cầu thủ | Chuyển CLB ngay kèm phí và % bán lại (`+0xC4` trong terms); cho mượn; chuyển nhượng tương lai đủ điều khoản (trả góp, tiền theo trận/bàn, phí mua lại, bán lại, đóng góp lương, giao hữu) và xoá chuyển tương lai; ký cầu thủ tự do; đổi chỗ hai cầu thủ; giải phóng hợp đồng; đội trưởng / đội phó; chép chỉ số và so sánh cầu thủ |
| Nhân sự | Chỉ số, CA/PA, tính cách, training rating HLV, số trận và bàn thắng quốc tế, bằng huấn luyện (kể cả *"Đang học…"*), 15 điểm vai trò, hợp đồng, kế hoạch sự nghiệp, quan hệ, ngôn ngữ, hiểu biết, sơ đồ và xu hướng; chuyển sang CLB khác kèm vai trò và lương mới |
| CLB | Thông tin, cơ sở vật chất (gồm Corporate Facilities), tài chính, nợ, CLB liên kết, tầm nhìn CLB, áo đấu và màu, quan hệ, nhân sự, đội hình và trung tâm chuyển nhượng |
| Thế giới và giải đấu | Quốc gia (thoả thuận, giá trị chuyển nhượng, ưu tiên, EuroCoefficients, đội tuyển), sân (chủ sở hữu, vị trí, sức chứa, kích thước, toạ độ), thành phố (quốc gia, vùng, ngôn ngữ, khí hậu), giải đấu (VAR, tiền thưởng, quy định đội hình, BXH), giải thưởng (`AWARD_MANAGER`), loại chấn thương và trọng tài *(bản FM27 đầy đủ)* |
| Công cụ | Preset cả đội có xem trước và Undo (hồi phục, thể lực, tinh thần, xoá treo giò, xoá bất mãn, work permit, Golden Generation, chặn giải nghệ, max chỉ số, tactical understanding, team cohesion), Mass Edit (`Ctrl+M`), Hook Manager (Wonderkid Academy, Development Speed), khoá giá trị, xuất/nhập CSV, JSON và chỉ số, giao diện Sáng/Tối, song ngữ Anh/Việt, quy đổi tiền tệ |

## Điểm nổi bật

### Mass Edit (`Ctrl+M`)
Sửa hàng trăm cầu thủ, nhân sự, CLB hay quốc gia cùng lúc bằng bộ lọc của Advanced Search. Chọn **Set to** hoặc **Change by**, xem trước từng dòng cũ → mới và bỏ tick phần không muốn trước khi ghi; hỗ trợ Undo cả lượt, theo từng người hoặc theo từng trường.

### Training Rating của HLV
FM tính số sao huấn luyện từ chỉ số chuyên môn kết hợp với **Determination**, **Authority** và **Motivating** (tính hai lần). Editor dùng đúng công thức này nên số sao khớp với game:

| Mảng | Chỉ số nặng ký nhất |
|---|---|
| Thủ môn: Shot Stopping | Goalkeeping (×9) |
| Thủ môn: Handling and Distribution | Goalkeeping (×6), tối đa 4 sao |
| Phòng ngự, Tấn công (Technical / Tactical) | Defending hoặc Attacking (×6) cộng Technical hoặc Tactical (×3) |
| Kiểm soát bóng (Technical / Tactical) | Possession (×3) cộng Technical hoặc Tactical (×6) |
| Sức mạnh, Tốc độ | Fitness (×9) |
| Bóng chết | Set Pieces (×3) cộng Tactical Knowledge (×6) |

Khi kéo thanh trượt và Save, các chỉ số nền được đặt theo `số sao × 4`. Trong Mass Edit, "Change by" cộng/trừ vào số sao hiện tại trong khoảng 0–5.

### Runtime hooks (Windows x64 và macOS ARM64)
- **Wonderkid Academy (Thử nghiệm):** định hình PA ngẫu nhiên (`CA..200`) và tỉ lệ Wonderkid % nâng CA khởi đầu cho các lứa trẻ/regen mới sinh ra, tuỳ chọn áp cho toàn cầu hoặc theo châu lục/quốc gia/CLB. Không đổi cầu thủ đã có.
- **Development Speed:** tăng tốc độ tiến bộ khi tập luyện (`1` bình thường, tối đa `20`) ngay trên thẻ Tools của cầu thủ.

### Tiền tệ, thay đổi chưa lưu và CA drift
- Tiền được hiện theo đơn vị FM đang dùng (tự nhận diện) hoặc đơn vị bạn chọn trong Settings, quy đổi theo tỷ giá cố định của game trong khi vẫn giữ đơn vị bảng Anh bên trong save.
- Mọi tab con của cầu thủ hay HLV hiện dấu `*` khi có chỉnh sửa chưa lưu.
- **Cách CA hoạt động trong FM:** game coi CA là chuẩn gốc; qua vài ngày trong game, các chỉ số sẽ tự trôi về mức tương ứng với CA, nên hãy chỉnh cả CA nếu muốn giữ chỉ số lâu dài.

## Tự build và công cụ dòng lệnh

Yêu cầu: [.NET 10 SDK](https://dotnet.microsoft.com/download) (trên macOS cần thêm Xcode Command Line Tools để đóng gói helper và bundle).

```bash
dotnet build Fm27Editor.slnx -c Release
dotnet test Fm27Editor.slnx -c Release
dotnet run --project src/Fm27Editor.App -c Release
```

Đóng gói bản macOS NativeAOT `.app` và `.dmg` (`dist/Fm27Editor-mac-arm64.dmg`):

```bash
VERSION=1.5.3 scripts/package-mac.sh
```

Chạy `Fm27Editor.Tools` khi game đang mở và đã nạp save (lệnh ghi cần cờ `--write`, không có sẽ chỉ in kế hoạch):

```bash
dotnet run --project src/Fm27Editor.Tools -c Release -- players "Kobbie Mainoo"
dotnet run --project src/Fm27Editor.Tools -c Release -- set "Kobbie Mainoo" Crossing 12 --write
dotnet run --project src/Fm27Editor.Tools -c Release -- gamecheck
```

| Lệnh | Chức năng |
|---|---|
| `players [tên]` | Liệt kê cầu thủ khớp kèm ngày game, tuổi, CLB, lương, hạn hợp đồng, CA/PA |
| `set <tên> <trường> <giá trị> [--write]` | Sửa chỉ số, vị trí, điểm tính cách, `CA` hoặc `PA` |
| `verify <golden.json>` | So sánh bộ nhớ đang chạy với dữ liệu chép từ UI game |
| `find-offsets <golden.json> <out.json> [--date yyyy-MM-dd]` | Dựng lại layout cầu thủ từ dữ liệu mẫu |
| `newbuild [--out <path>] [--layout macos\|touch-macos\|windows]` | Tự quét RTTI vtable, root và offset dịch cho bản update mới (~30 giây) |
| `verify-build --profile <path> [--expect-players N ...] [--writes] [--negative-test] [--crash-gate ...]` | Cổng phát hành trả về một kết luận PASS/FAIL |

Các lệnh khác (nhân sự, CLB, quốc gia, preset, lời hứa, chuyển nhượng...) được liệt kê ở đầu `src/Fm27Editor.Tools/Program.cs`.

## Cấu trúc

| Đường dẫn | Nội dung |
|---|---|
| `src/Fm27Editor.Core` | Bộ nhớ process, kiểm tra build, offset profiles, đọc/ghi có kiểm tra, Undo journal |
| `src/Fm27Editor.App` | App desktop (Avalonia 11, viết thuần code) |
| `src/Fm27Editor.Tools` | CLI kiểm tra, `newbuild` và cổng phát hành `verify-build` |
| `src/Fm27Editor.Helper` | Privileged memory helper cho macOS (`fm27_helper.c`) |
| `offsets/<build>.json` | Profile offset nhúng theo từng build (thư mục `offsets/` cạnh file chạy ghi đè bản nhúng) |
| `offsets/layouts/*.json` | Layout nền theo từng nền tảng (`windows.json`, `macos.json`, `touch-macos.json`) |
| `tests/` | Unit test Core và App (`Fm27Editor.Core.Tests`, `Fm27Editor.App.Tests`) |
| `scripts/package-mac.sh` | Script đóng gói `.app` và `.dmg` NativeAOT cho macOS |
| `re/tools/memscan` | Công cụ quét bộ nhớ chỉ đọc phục vụ đo offset |

## Khi game có patch

App không gắn vào bản game chưa có profile. Để hỗ trợ bản mới, nạp một save thử rồi chạy (thêm `dotnet run --project src/Fm27Editor.Tools -c Release --` phía trước hoặc dùng `Fm27Editor.Tools.exe` trên Windows):

1. `newbuild --out offsets/<build>.json` tự quét mọi RTTI vtable, root và offset bị dịch trong ~30 giây (`"readOnly": true`). Dòng nào báo `MANUAL` thì xử lý theo gợi ý in kèm.
2. `verify-build --profile offsets/<build>.json --expect-players N --expect-staff N --expect-humans N --negative-test --writes` kiểm vtable, số lượng đối chiếu từ game, tính hợp lệ dữ liệu, mốc dữ liệu cố định, round-trip ghi từng byte và heap của game.
3. `verify-build ... --crash-gate state.json`, bấm **Continue** rồi **Load Game** trong FM, rồi chạy `verify-build ... --crash-gate-verify state.json --release-gate --receipt receipt.json`. Receipt `VERDICT: PASS` là đủ điều kiện bỏ `"readOnly"` và tạo PR.

## Chưa hỗ trợ: bóng đá nữ

Editor chỉ được xây dựng và kiểm chứng cho bóng đá nam. Đội, cầu thủ và giải đấu nữ có thể thiếu hoặc sai dữ liệu và chưa được kiểm tra thao tác ghi, nên tránh chỉnh sửa chúng.

## Lộ trình

Đã xong:
- Đa nền tảng Windows (Steam và Xbox / Game Pass) và macOS Apple silicon (FM27 Steam và FM27 Touch).
- Cầu thủ, nhân sự, CLB, quốc gia, sân, thành phố, giải đấu, giải thưởng, trọng tài và loại chấn thương, gồm cả cầu thủ kiêm nhân sự.
- Chuyển nhượng ngay (phí và % bán lại), cho mượn, chuyển nhượng tương lai đủ điều khoản, ký tự do, đổi người, giải phóng hợp đồng và cấp phát trên heap của game trên macOS.
- Tự dò profile `newbuild` và cổng kiểm chứng `verify-build` / crash-gate.
- Mass Edit (Cầu thủ, Nhân sự, CLB, Quốc gia), training rating HLV, preset đội hình, freezer, Wonderkid Academy và Development Speed.
- Giao diện Sáng/Tối, song ngữ Anh/Việt, quy đổi tiền tệ và role scores tích hợp sẵn.

Tiếp theo:
- Tab biểu đồ radar chỉ số và trực quan hoá cầu thủ.
- Shortlist.
- Đủ điều khoản chuyển nhượng cho thao tác chuyển nhượng ngay (hiện hỗ trợ % bán lại; chuyển nhượng tương lai đã có đủ điều khoản).
- Tab Scout Information/Stats và Possible Signings.
- Item áo đấu (đổi loại, thêm, xoá, nhân bản).
- Lịch tập và phụ cấp HLV (coach allowances).
- File exe Windows nhỏ và khởi động nhanh hơn.
- Bóng đá nữ (dự kiến sau).

## Tuyên bố miễn trừ và báo lỗi

Dự án không liên kết hay được bảo trợ bởi Sports Interactive hoặc SEGA; Football Manager là nhãn hiệu của họ. Can thiệp RAM có thể làm hỏng save, hãy luôn sao lưu trước khi dùng và thử trên save nháp nếu chưa chắc. **Không bảo hành, không chịu trách nhiệm:** phần mềm cung cấp "nguyên trạng".

Nếu save bị lỗi hoặc crash, bấm biểu tượng **con bọ** ở góc dưới bên phải app để xuất file zip chẩn đoán ra Desktop (gồm mô tả của bạn, trạng thái app/game và nhật ký ghi của 10 lần chạy gần nhất tại `%LOCALAPPDATA%\Fm27Editor\logs`). Không kèm file save, không tự upload và tên thư mục được cắt chỉ còn tên cuối. Kiểm tra lại file zip rồi gửi kèm tên bản game bạn dùng (Steam, Game Pass hoặc Mac/Touch) qua [GitHub Issues](https://github.com/Jackhamerp24/Football-Manager-27-Editor/issues) hoặc email **vuquyduong2004@gmail.com**.

## AI Acknowledgments

Dự án có sử dụng hỗ trợ của các mô hình AI trong quá trình phát triển:

- **Claude:** Haiku / Sonnet / Opus 5.5
- **Codex / ChatGPT:** Terra, Sol 6.1, 6 Astra, 6.2 Sol/Astra
- **Gemini / Antigravity:** Gemini 3.8 Flash, 4 Argon, 4 Hydrogen, 4 Neon
- **OpenCode Go:** DeepSeek v4.1 Flash 1017, MiMo v2.6 Flash, Muse Spark 1.3 Contributor, LongCat 2.5, GLM 5.3
- **Muse Code:** Muse Wonder 1.0, Muse Spark 1.3 Contributor
- **Logo ứng dụng:** Tạo bằng ChatGPT Image

## Ủng hộ

Nếu editor giúp ích cho bạn, có thể ủng hộ tác giả qua:

- [Ko-fi](https://ko-fi.com/quyduongvu) (thẻ hoặc PayPal)
- Chuyển khoản ngân hàng Việt Nam (VietQR, Vietcombank): quét bằng app ngân hàng hoặc ví điện tử bất kỳ

<img src="docs/images/donate-vietqr.png" alt="VietQR, Vietcombank" width="220">

## Giấy phép

GPL-3.0-or-later. Xem [LICENSE](LICENSE).
