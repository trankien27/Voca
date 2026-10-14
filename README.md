# Voca 2

Ứng dụng Windows học từ vựng tiếng Anh mỗi ngày, **chạy độc lập hoàn toàn**: không cần server, không cần tài khoản, dùng được khi mất mạng. Từ vựng nằm ngay trên taskbar, mỗi ngày một phiên học ngắn có kiểm tra, ôn tập ngắt quãng tự động, và tạo chủ đề mới bằng bất kỳ AI chat nào (không cần API key).

## Chạy

```powershell
dotnet run --project .\Voca.csproj
```

Bản phát hành (một file exe, cần .NET 8 Desktop Runtime). Script chạy kiểm tra rồi build vào `dist\v<phiên bản>\`,
phiên bản lấy từ `<Version>` trong `Voca.csproj` — tăng số này trước khi build bản mới để giữ các bản cũ:

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

Phát hành bản mới lên GitHub Releases (các máy chạy Voca 2.7.0 trở lên thấy bản này trong *Cài đặt → Cập nhật phiên bản*):

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Notes "Mô tả thay đổi"
```

Cần GitHub CLI đã đăng nhập (`winget install --id GitHub.cli` rồi `gh auth login`), code đã commit và push, và `<Version>` mới trong `Voca.csproj`.

Kiểm tra hành vi (244 kiểm tra cho các quy tắc nghiệp vụ):

```powershell
dotnet run --project .\tests\Voca.Checks
```

## Tính năng

**Học mỗi ngày**
- Lần mở đầu có sẵn khóa học 7 tuần (Education, Work & Money, Health, Technology, Environment, Society & Government, IELTS), mỗi tuần 7 ngày × 20 từ, đủ phiên âm, nghĩa và ví dụ.
- Chữ trên taskbar xoay các từ của hôm nay (từ trả lời sai lên trước). Chế độ *chỉ từ*, *flashcard từ → nghĩa*, *nghĩa → từ*. Tự ẩn khi xem video hay trình chiếu toàn màn hình. Kéo để đổi vị trí.
- Bấm vào chữ: chi tiết từ, phát âm, đang ở **Tuần · Ngày x/7**, nút **Bắt đầu phiên học**.
- **Phiên học**: lật thẻ học từ mới (Space, ←/→), rồi kiểm tra xoay vòng 4 dạng: chọn từ theo nghĩa, chọn nghĩa của từ, chọn từ điền vào chỗ trống trong câu ví dụ, và gõ từ khi nhìn nghĩa (phím 1–4, Enter). Tắt hai dạng sau ở *Cài đặt → Phiên học*. App tự chấm: đúng thì giãn lịch ôn (2, 6, 15… ngày), sai thì ôn lại ngày mai.
- **Thẻ lật trên popup**: bấm chữ trên taskbar mở popup với từ, phiên âm và câu ví dụ; nghĩa tiếng Việt nằm úp trên một thẻ (*Nghĩa là gì? Bấm để lật*) để tự nhớ trước, bấm thẻ thì lật ra nghĩa — kèm **câu ví dụ dịch sang tiếng Việt** ngay dưới câu tiếng Anh, để biết cách dịch cả câu — bấm lần nữa úp lại. Mỗi lần mở popup hay chuyển từ (‹ ›) thẻ lại úp. Thẻ trong phiên học cũng hiện bản dịch câu ví dụ khi lật.
- **Đánh giá nhanh trên popup**: **Đã thuộc** — từ không hiện lại nữa (không vào phiên học, ôn tập), có **Hoàn tác**; bỏ tích ở cột *Đã thuộc* trong Sửa lộ trình để học lại. **Chưa nhớ** — tính như trả lời sai: hôm nay hiện trước, mai ôn lại, vào danh sách từ sai. Đánh dấu thuộc từ cuối cùng của ngày thì lộ trình sang ngày tiếp ngay (Hoàn tác cũng đưa về lại ngày đó).
- **Từ của tôi** (Ctrl+Alt+V, tab *Từ của tôi*): bôi đen một từ ở bất kỳ đâu (trình duyệt, Word, PDF…) rồi nhấn **Ctrl+Alt+V** — một cửa sổ nhỏ hiện cạnh con trỏ với từ đó và các ô Phiên âm, Loại từ, Nghĩa, Ví dụ, Dịch ví dụ (🔊 để nghe). **Lưu** (Enter): từ vào lộ trình "Từ của tôi" (ngoài khóa học, mỗi ngày một nhóm) và vào ôn tập ngay hôm nay. Thư viện đã có từ đó thì cửa sổ báo nằm ở lộ trình/ngày nào kèm nghĩa đã lưu, không thêm trùng. Không bôi đen gì thì cửa sổ mở trống để gõ. **Để AI điền sau**: đưa vào danh sách chờ; ở tab *Từ của tôi* bấm **Sao chép prompt** để AI điền cả danh sách, dán câu trả lời → **Nhập** → **▶ Học ngay**. App chỉ lấy đúng chữ đang bôi đen (không dùng nội dung copy cũ) và trả lại clipboard như trước; trong cửa sổ dòng lệnh thì dùng chữ đã copy.
- **Phụ đề video** (tab *Phụ đề video* trong Thư viện; mở nhanh bằng nút phụ đề (CC) trên popup hoặc menu khay → *Phụ đề từ video…*): chọn video (mp4, mkv, mov…, hoặc file âm thanh) nói tiếng Anh → **Tạo phụ đề**: Voca nhận dạng giọng nói ngay trên máy bằng Whisper (không cần mạng, không cần API key) và lưu `tên-video.srt` cạnh video (trình phát tự nhận). Lần đầu tải model một lần: *base.en* (~142 MB, nhanh) hoặc *small.en* (~466 MB, chính xác hơn), lưu trong thư mục dữ liệu `models\`. Mỗi đoạn được nhận dạng độc lập (không lấy câu trước làm gợi ý) và các chuỗi một câu lặp từ 4 lần liên tiếp trở lên bị bỏ, nên Whisper không còn "nghe" một câu lặp suốt nhiều phút trên nền nhạc. Tùy chọn **Tạo bộ từ** — **mỗi video một bộ từ riêng**, không gộp: chọn video trong danh sách rồi *Tìm từ mới* liệt kê các từ của riêng video đó chưa có trong thư viện, trong *Từ của tôi* hay trong bộ từ đang chờ của video khác. Bấm *Sao chép prompt* là bộ từ được **lưu ngay vào dữ liệu Voca** (trạng thái *đang chờ AI*) nên video khác không gợi ý lại các từ đó, kể cả sau khi tắt app; mở lại video sẽ thấy bộ từ đang chờ (từ đã tích sẵn) để dán câu trả lời, hoặc *Bỏ bộ từ đang chờ* để trả các từ về. Nhập xong, bộ từ ghi lại lộ trình đã tạo từ video đó. Danh sách từ mới (gộp các dạng như postponed/postponing, bỏ từ quá phổ biến và tên riêng, từ hay gặp nhất ở trên, kèm câu trong video) → tích từ muốn học → **Sao chép prompt** cho AI chat → dán câu trả lời → **Nhập vào khóa học**. **Nhiều video cùng lúc**: giữ Ctrl/Shift khi chọn để chọn nhiều video — danh sách hiện trạng thái từng video, *Tạo phụ đề* chạy lần lượt cả loạt (video lỗi được bỏ qua, *Hủy* dừng cả hàng đợi), mỗi video lưu .srt cạnh nó; video đã có .srt được bỏ qua trừ khi tích *Tạo lại*; bấm một video trong danh sách để xem phụ đề, phát hoặc tìm từ mới của video đó. Cũng mở được file .srt có sẵn để chỉ tạo bộ từ; chọn video mà cạnh đó đã có .srt cùng tên thì phụ đề được mở luôn, không cần tạo lại.
- **▶ Xem video có phụ đề** (tab *Phụ đề video*): phát video kèm phụ đề chạy theo. Trong phụ đề, **từ mới** của video tô **vàng đậm** (bấm vào để lưu ngay vào *Từ của tôi*), từ **đang học** (có trong thư viện, chưa thuộc) tô **tím đậm** (rê chuột xem nghĩa), từ đã lưu chờ điền nghĩa tô **xanh**. Rê chuột vào phụ đề thì video tạm dừng để đọc. Phím: Space phát/dừng, ←/→ tua 5 giây, *⏮ câu* nghe lại câu, F toàn màn hình, C bật/tắt phụ đề, R mở *Đọc phụ đề*; tốc độ 0.5×–1.5×. Video cần định dạng Windows phát được (mp4 H.264 chắc chắn được).
- **Thư mục phim** (dòng *Phim* đầu tab *Phụ đề video*, hoặc *Cài đặt → Phim*; ví dụ `D:\Film`): mỗi thư mục con là một phim, tên thư mục là tên phim, bên trong là các tập (cả trong thư mục con của nó); video để lẻ trong thư mục phim là một phim riêng. Chọn phim ở dòng *Phim* là danh sách tập hiện ra (tên rút gọn như *E01*, kèm *đang xem 12:30* / *đã xem ✓* và *Đã có phụ đề* / *Chờ*), tập nên xem tiếp được chọn sẵn; bấm một tập để xem phụ đề và *▶ Xem video có phụ đề*; *Tạo phụ đề* tạo lần lượt cho các tập chưa có. Nút **Tạo phụ đề cả thư mục** quét mọi phim trong thư mục phim và tạo lần lượt phụ đề cho mọi tập còn thiếu (từng tập một, phim đang xem dở trước; *Hủy* để dừng, bấm lại thì làm tiếp phần còn thiếu). Ô *Model* chọn sẵn model đã tải trên máy.
- **Xem tiếp** (nút đầu tab *Phụ đề video*): Voca nhớ từng video đã xem tới đâu: đang phát thì cứ 5 giây lưu một lần, và lưu ngay khi dừng, khi đóng cửa sổ xem hay khi xem hết — app có bị tắt ngang (crash, mất điện) cũng chỉ mất vài giây. Vị trí xem nằm trong file nhỏ `watch.json` cạnh `voca.json` (chỉ của máy này, không đi theo khi xuất sang máy khác). Nút *Xem tiếp* mở lại video xem gần nhất kèm phụ đề, chạy tiếp đúng phút giây lần trước dừng; nếu tập đó đã xem hết (còn dưới 30 giây) thì nút thành *Xem tập tiếp* — mở file video kế tiếp trong cùng thư mục (theo thứ tự tên, E2 trước E10). Mở lại một video đã xem dở bằng *▶ Xem video có phụ đề* cũng chạy tiếp từ chỗ cũ; kéo thanh thời gian để xem từ đầu.
- **Đọc phụ đề** (tab *Phụ đề video*, hoặc nút *Đọc phụ đề* khi xem video): mở phụ đề của tập phim thành bài đọc để luyện reading — **chỉ cần file .srt**, không cần video (chưa mở phụ đề nào thì nút hỏi chọn file .srt). Các câu được ghép thành đoạn văn (xuống đoạn ở chỗ ngừng nói), chữ lớn dễ đọc; từ mới, từ đang học và từ đã lưu được tô màu như khi xem video (bấm từ mới để lưu vào *Từ của tôi*, rê chuột lên từ đang học xem nghĩa). Tắt *Tô màu từ* để đọc như văn bản thường, bật *Mốc thời gian* để thấy mỗi đoạn ở phút nào. Ctrl+F tìm trong bài, Ctrl + lăn chuột phóng to chữ.
- **Bài kiểm tra** (nút bài kiểm tra trên popup, menu khay, Thư viện → *Khóa học* → *Tạo bài kiểm tra*, hoặc *Sửa lộ trình* → *Kiểm tra ngày này*): chọn lộ trình, khoảng ngày, số câu (10/20/30/40/tất cả) và dạng câu — trắc nghiệm từ → nghĩa, nghĩa → từ, gõ từ khi nhìn nghĩa (có gợi ý chữ cái đầu), nghe phát âm rồi chọn từ. Làm như bài thi: chọn đáp án là tự sang câu tiếp, "Câu tiếp" luôn bấm được (bỏ trống được), "Câu trước" để quay lại sửa; chỉ chấm điểm khi bấm **Nộp bài** (câu bỏ trống tính sai). Cuối bài có điểm, thời gian, điểm theo từng dạng và danh sách câu sai; làm lại câu sai. Kết quả không làm thay đổi lịch ôn; từ sai được lưu vào danh sách từ sai.
- **Tất cả từ** (tab *Tất cả từ* trong Thư viện): mọi từ của mọi lộ trình trong một bảng, kèm lộ trình/ngày, trạng thái (*Chưa học*, *Đang học*, *Đến hạn ôn*, *Nhớ lâu* — lần ôn tới cách 21 ngày trở lên, *Đã thuộc*), số lần đúng/sai và ngày ôn tiếp. Tìm theo từ hoặc nghĩa (gõ không dấu cũng được), lọc theo trạng thái và lộ trình, sắp xếp theo lộ trình, mới gặp gần đây, A → Z hoặc hay quên nhất. Chọn từ để **Phát âm**, **Học các từ đã chọn** (Ctrl/Shift để chọn nhiều; không ảnh hưởng bài học hôm nay) hoặc nhấp đúp để mở trong *Sửa lộ trình*. Bảng tải dần 50 từ một lần khi cuộn xuống; bấm tiêu đề cột để sắp xếp toàn bộ danh sách. **Kiểm tra từ trùng** liệt kê các từ có mặt hơn một lần trong thư viện (khác lộ trình hoặc ngày, không phân biệt hoa thường và dấu câu ở cuối), các bản nằm cạnh nhau để mở và xóa bản thừa; dòng tóm tắt đầu tab luôn báo số từ bị trùng.
- **Danh sách từ sai** (tab *Từ sai* trong Thư viện; mở nhanh bằng dòng "… từ sai" trên popup, menu khay hoặc cuối bài kiểm tra): mỗi bài kiểm tra tạo một danh sách các từ làm sai, từ sai trong phiên học hằng ngày gom theo ngày, kèm mục "Tất cả từ sai". Chọn một danh sách để **Học ngay** (lật thẻ rồi trắc nghiệm, không ảnh hưởng bài học hôm nay), **Để ngày mai** (thành ngày học từ sai) hoặc xóa. Từ trả lời đúng tự ra khỏi mọi danh sách.
- **Ngày học từ sai**: một danh sách hẹn sang ngày mai sẽ thay cho bài của lộ trình hôm đó (tối đa 30 từ), lộ trình tạm nghỉ và hôm sau học tiếp; trên popup có thể học ngay hôm nay, dời hoặc hủy. Từ sai mới trước ngày đó được tự thêm vào. Ngày học từ sai chỉ có hiệu lực đúng ngày đã hẹn; bỏ lỡ thì lộ trình vẫn chạy tiếp, các từ vẫn nằm trong tab Từ sai.
- **Mỗi từ có trạng thái đã thuộc / chưa thuộc.** Lộ trình chỉ sang ngày tiếp khi **mọi từ của ngày đều được đánh dấu Đã thuộc** — sang ngay lúc đó, không chờ hôm sau; chưa thuộc hết thì dù qua bao nhiêu ngày vẫn ở ngày cũ, các từ chưa thuộc tiếp tục hiện trên taskbar và trong phiên học. Popup ghi *Đã thuộc x/y từ*; khi sang ngày có thông báo. Đánh dấu thuộc bằng nút trên popup, cột *Đã thuộc* trong Sửa lộ trình, hoặc tích **Đã thuộc** ở màn kết quả phiên học (có nút *Tích các từ trả lời đúng*; lưu khi đóng cửa sổ). Hết tuần hiện tổng kết và tự sang tuần kế.
- Ngày bị bỏ qua bởi bản cũ (bản cũ tự sang ngày mỗi ngày mới): từ của ngày đó không hiện lên taskbar; popup có nút **Ngày N chưa học · Học bù** để học lại ngày bỏ qua gần nhất.
- Nhắc học bằng thông báo Windows lúc bắt đầu ngày và vào giờ tự chọn buổi tối (nếu chưa học).
- **Thống kê** (nút biểu đồ trên popup): streak, kỷ lục, số từ đã thuộc/đang học/đến hạn, 30 ngày, từ hay quên, nút **Tạo tuần ôn** từ những từ hay quên.

**Thư viện** (chuột phải chữ trên taskbar, nút bánh răng trên popup, hoặc nhấp đúp icon khay)
- **Khóa học**: thứ tự các tuần (đổi thứ tự các tuần đang chờ), bỏ khỏi / thêm vào khóa học.
- **Sửa lộ trình**: sửa trực tiếp từng ô, thêm/xóa từ, thêm ngày, đặt tiêu đề ngày, **Học từ ngày này**, **Tạo lại ngày này bằng prompt**, **Xuất ra file .md**, xóa lộ trình.
- **Tạo chủ đề mới**: nhập chủ đề, số ngày, số từ, trình độ → **Sao chép prompt** → dán vào Claude/ChatGPT/Gemini → dán câu trả lời → **Xem trước** (báo lỗi và cảnh báo: thiếu từ, thiếu phiên âm/nghĩa/ví dụ/dịch ví dụ, trùng từ…) → **Nhập vào khóa học**. Form có 7 cột — STT, Từ, Phiên âm, Loại, Nghĩa, Ví dụ, **Dịch ví dụ** — và mọi prompt (chủ đề mới, bộ từ từ video, Từ của tôi) đều yêu cầu AI dịch câu ví dụ; form 6 cột cũ vẫn nhập được. *Sửa lộ trình* có cột *Dịch ví dụ* để sửa tay. Cũng mở được file `.md`/`.json` đã xuất. Đầu tab có hướng dẫn từng bước (đánh dấu bước đang làm, nút mở Claude/ChatGPT/Gemini, mẹo và form mẫu); ẩn được và app nhớ lựa chọn.
- **Cập nhật phiên bản** (*Cài đặt → Cập nhật phiên bản*, hoặc menu khay → *Cập nhật phiên bản…*): danh sách các bản phát hành trên GitHub (ngày, dung lượng, ghi chú; đánh dấu "Mới nhất" và "Đang dùng"). Chọn một bản → **Cập nhật lên…** (hoặc **Chuyển về…** với bản cũ hơn): app tự tải, chỉ dùng file khi mã SHA-256 và số phiên bản khớp, thay `Voca.exe` (bản cũ giữ tạm thành `Voca.exe.old`) rồi tự mở lại. Dữ liệu trong `%LOCALAPPDATA%\Voca` không bị đụng. Mỗi ngày app tự xem có bản mới không (tắt được) và báo bằng dòng "Có Voca X" trên popup và thông báo khay một lần — không tự tải. Mở bản nào thì mục "Chạy cùng Windows" (nếu đang bật) tự trỏ sang bản đó, để bản cũ không tự bật lại. Các trường dữ liệu do bản mới hơn ghi được giữ nguyên khi chạy bản cũ hơn (từ 2.8.2).
- **Cài đặt**: hiển thị, thời gian đổi từ, số từ ôn tối đa mỗi ngày, nhắc học, chạy cùng Windows, **kiểu chữ trên taskbar** (phông, cỡ, đậm/nghiêng, màu chữ và màu đáp án, đổ bóng, nền sau chữ và độ đậm nền, độ rộng tối đa — có xem trước trực tiếp và nút về mặc định), mở thư mục dữ liệu, sao lưu.

## Học trên điện thoại

**Thư viện → Cài đặt → Học trên điện thoại** → tích **Cho phép điện thoại kết nối** → quét mã QR bằng camera điện thoại. Voca (đang chạy trên máy tính) mở một trang web nhỏ cho điện thoại, dùng **chính dữ liệu của máy này** — học trên điện thoại thì máy tính thấy ngay, không có hai bản tiến độ:
- **Hôm nay**: từ của hôm nay như trên taskbar (từ trả lời sai lên trước), thẻ lật nghĩa (chạm để lật, vuốt trái/phải để chuyển từ), 🔊 phát âm bằng giọng đọc của điện thoại, **✓ Đã thuộc** (có Hoàn tác) và **✗ Chưa nhớ**; vị trí khóa học, streak và nút **Bắt đầu phiên học**.
- **Phiên học**: lật thẻ từ mới rồi kiểm tra 4 dạng như trên máy tính; kết quả tính vào lịch ôn, tích *Đã thuộc* ở màn kết quả.
- **Video**: xem video **kèm phụ đề** ngay trên điện thoại — video được phát từ máy tính (tua được, không cần chép sang điện thoại). **Thư mục phim** (*Cài đặt → Phim → Chọn thư mục phim…*, ví dụ `D:\Film`): mỗi thư mục con là một phim, tên thư mục là tên phim, bên trong là các tập (cả trong thư mục con của nó); video để lẻ trong thư mục phim là một phim riêng. Điện thoại hiện danh sách phim (phim đang xem lên đầu, ghi *Đang xem E04 · 12:30* hoặc *Tiếp theo: E05*), chạm một phim để thấy các tập — tên tập được rút gọn (`Naruto_Shippuden_[English]_720P_S01_E01.mp4` → *E01*). Ngoài ra còn các video trong thư mục bạn đã xem hoặc tạo bộ từ trên máy tính và các thư mục thêm bằng *Thêm thư mục video…*. Video có file `.srt` cùng tên thì có phụ đề. Voca dùng **trình phát riêng** (không dùng nút điều khiển của trình duyệt): chạm video để hiện/ẩn nút, chạm đúp nửa trái/phải để lùi/tới 10 giây, thanh tua có phần đã tải, tốc độ, CC, **Aa** (cỡ phụ đề nhỏ / vừa / lớn, điện thoại nhớ lựa chọn), *⏮ Câu* và ⛶ **toàn màn hình ngang** (Android: toàn màn hình thật và xoay ngang; iPhone: phủ kín màn hình, xoay ngang điện thoại), thẻ nghĩa và thông báo vẫn hiện khi toàn màn hình, hết tập có nút *Tập tiếp*. Phụ đề nằm dưới video (xoay ngang hay toàn màn hình thì nằm trên video), tô màu như trên máy tính: **từ mới** vàng (chạm → *Lưu vào Từ của tôi*), **từ đang học** tím (chạm → xem nghĩa, phát âm), từ **đã lưu** xanh; chạm từ thì video tạm dừng, *Tiếp tục xem* để chạy tiếp. Bên dưới là toàn bộ phụ đề, câu đang nói được tô, chạm một câu để tua tới đó. Có *⏮ Câu* (nghe lại câu), lùi 5 giây, tốc độ 0.5×–1.5×, bật/tắt phụ đề. Vị trí xem được lưu vào cùng `watch.json` với máy tính: xem dở trên điện thoại thì *Xem tiếp* trên máy tính chạy tiếp đúng chỗ đó, và ngược lại; xem hết thì có nút sang tập tiếp. Điện thoại phát tốt nhất là **mp4 (H.264)**; mkv/avi có thể không phát được.
- **Từ vựng**: tìm mọi từ theo từ hoặc nghĩa (gõ không dấu được), lọc theo trạng thái, chạm để xem chi tiết.
- **Thêm từ**: lưu từ kèm nghĩa vào *Từ của tôi* (vào ôn tập ngay), hoặc **Để AI điền sau**; báo nếu thư viện đã có từ đó.

Điện thoại cần **cùng mạng Wi-Fi/LAN** với máy tính; muốn dùng khi ra ngoài thì cài [Tailscale](https://tailscale.com) trên cả hai máy và chọn địa chỉ Tailscale trong danh sách. Lần đầu bật, Windows có thể hỏi về tường lửa: chọn cho phép trên **mạng riêng** (Private). Link trong mã QR có một mã bí mật — điện thoại không có mã bị từ chối; **Đổi mã kết nối** để ngắt mọi điện thoại đã kết nối. Mặc định dùng cổng 8765 (đổi được). Mở trang xong, *Thêm vào màn hình chính* để lần sau mở như một ứng dụng.

## Chuyển sang máy khác

**Máy gửi**: Thư viện → *Khóa học* → **Xuất / chép sang máy khác…** → chọn lộ trình (mặc định cả khóa học, theo thứ tự), rồi:
- **Lưu file .voca…** — chuyển đầy đủ; có thể **kèm tiến độ học** (lịch ôn từng từ, streak, vị trí đang học). Chép file qua USB, email, Drive…
- **Sao chép dạng văn bản** — nhiều lộ trình theo form mẫu, dán qua chat/email (chỉ có từ, không có tiến độ).

**Máy nhận**: Thư viện → *Cài đặt* → **Nhập từ máy khác…** (chọn file .voca), hoặc dán văn bản vào *Tạo chủ đề mới* → **Xem trước** → **Nhập**.
- Lộ trình đã có (cùng tên hoặc ≥ 80% từ giống nhau) **không bị trùng**; tiến độ trong file chỉ được dùng khi **mới hơn** tiến độ trên máy nhận.
- Lộ trình mới được thêm vào cuối khóa học theo đúng thứ tự trong file. Nếu file có vị trí đang học, app hỏi có chuyển theo không.

## Chuyển từ Voca 1

Lần mở đầu tiên, nếu máy có dữ liệu Voca 1 (`%LOCALAPPDATA%\VocaTaskbar\vocabulary.json`), v2 tự nhập:
- mọi lộ trình Voca 1 đã đồng bộ (ví dụ các bộ tạo trên trang web cũ như *Du lịch*) — bộ trùng với khóa học có sẵn (cùng tên hoặc ≥ 80% từ giống nhau) chỉ được chép tiến độ, không tạo bản sao;
- tiến độ từng từ, nhật ký các ngày (streak), vị trí đang học, cài đặt hiển thị, vị trí chữ trên taskbar;
- từ đã học nhưng không thuộc lộ trình nào → lộ trình *Từ đã học ở Voca 1* (ngoài khóa học, vẫn được ôn).

Muốn chạy lại: **Thư viện → Cài đặt → Nhập lại từ Voca 1** (chạy nhiều lần không tạo bản sao). Từ nhập tay ở các bản rất cũ đã được Voca 1 xuất ra `vocabulary-local-export.md`; nhập file đó bằng **Tạo chủ đề mới → Mở file .md…**.

## Dữ liệu

Tất cả nằm trong `%LOCALAPPDATA%\Voca\voca.json`, ghi an toàn (file tạm + thay thế), luôn giữ bản `.bak`. File hỏng sẽ được giữ lại thành `voca.json.corrupt-<thời gian>` và tự khôi phục từ `.bak`.

## Form nhập (AI trả về đúng form này)

```markdown
# Tên bộ từ: Travel — 7 ngày × 20 từ
Cấp độ: B1–B2
Mô tả: Từ vựng du lịch cho giao tiếp và IELTS

## Ngày 1 — Sân bay và chuyến bay

| STT | Từ | Phiên âm | Loại | Nghĩa | Ví dụ |
|---|---|---|---|---|---|
| 1 | boarding pass | /ˈbɔːdɪŋ pɑːs/ | n | thẻ lên máy bay | Please show your boarding pass at the gate. |
```

## Cấu trúc mã

| Thư mục / file | Vai trò |
|---|---|
| `Models/AppData.cs` | Toàn bộ dữ liệu: cài đặt, lộ trình (ngày, từ), thứ tự khóa học, vị trí học, nhật ký |
| `Services/Store.cs` | Kho dữ liệu duy nhất dùng chung cho mọi cửa sổ, lưu an toàn, sự kiện `Changed` |
| `Services/CourseEngine.cs` | Quy tắc khóa học: từ hôm nay, chuyển ngày/tuần, sắp xếp, trắc nghiệm, ghi kết quả, tuần ôn |
| `Services/ReviewScheduler.cs` | Thuật toán ôn tập ngắt quãng (SM-2 rút gọn) |
| `Services/PlanFormat.cs` | Đọc/kiểm tra form (Markdown, JSON), tạo prompt, xuất form |
| `Services/SeedData.cs` + `Seed/*.md` | Khóa học có sẵn |
| `MainWindow` | Chữ trên taskbar, popup, nhắc học |
| `SessionWindow`, `WeekSummaryWindow`, `StatsWindow`, `LibraryWindow` | Phiên học, tổng kết tuần, thống kê, thư viện |
| `tests/Voca.Checks` | Kiểm tra hành vi |
