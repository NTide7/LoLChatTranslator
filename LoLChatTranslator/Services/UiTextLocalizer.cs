using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace LoLChatTranslator.Services;

public static class UiTextLocalizer
{
    private sealed record Entry(string ZhHans, string ZhHant, string En, string Ko, string Ja, string Vi)
    {
        public string For(string language)
        {
            return LocalizationService.NormalizeLanguage(language) switch
            {
                "zh-Hant" => ZhHant,
                "en" => En,
                "ko" => Ko,
                "ja" => Ja,
                "vi" => Vi,
                _ => ZhHans
            };
        }

        public IEnumerable<string> Values()
        {
            yield return ZhHans;
            yield return ZhHant;
            yield return En;
            yield return Ko;
            yield return Ja;
            yield return Vi;
        }
    }

    private static readonly Entry[] Entries =
    [
        E("恢复默认", "恢復預設", "Restore Defaults", "기본값 복원", "既定値に戻す", "Khôi phục mặc định"),
        E("取消", "取消", "Cancel", "취소", "キャンセル", "Hủy"),
        E("应用", "套用", "Apply", "적용", "適用", "Áp dụng"),
        E("保存", "儲存", "Save", "저장", "保存", "Lưu"),
        E("确定", "確定", "OK", "확인", "OK", "OK"),
        E("关闭", "關閉", "Close", "닫기", "閉じる", "Đóng"),
        E("更新", "更新", "Update", "업데이트", "更新", "Cập nhật"),
        E("删除", "刪除", "Delete", "삭제", "削除", "Xóa"),

        E("OCR 设置", "OCR 設定", "OCR", "OCR 설정", "OCR 設定", "OCR"),
        E("翻译设置", "翻譯設定", "Translation", "번역 설정", "翻訳設定", "Dịch"),
        E("过滤设置", "過濾設定", "Filters", "필터", "フィルター", "Bộ lọc"),
        E("显示设置", "顯示設定", "Display", "표시", "表示", "Hiển thị"),
        E("快捷键设置", "快捷鍵設定", "Hotkeys", "단축키", "ホットキー", "Phím tắt"),
        E("关于", "關於", "About", "정보", "情報", "Giới thiệu"),

        E("当前聊天区域", "目前聊天區域", "Current Chat Region", "현재 채팅 영역", "現在のチャット範囲", "Vùng chat hiện tại"),
        E("查看当前", "查看目前", "Preview", "미리보기", "表示", "Xem"),
        E("重新框选", "重新框選", "Select Again", "다시 선택", "再選択", "Chọn lại"),
        E("截图间隔（单位：ms）", "截圖間隔（單位：ms）", "Capture Interval (ms)", "캡처 간격(ms)", "キャプチャ間隔 (ms)", "Khoảng chụp (ms)"),
        E("启用实时低延迟 OCR", "啟用即時低延遲 OCR", "Enable Realtime Low-latency OCR", "실시간 저지연 OCR 사용", "リアルタイム低遅延 OCR を有効化", "Bật OCR thời gian thực độ trễ thấp"),
        E("启用 text_mask 检测", "啟用 text_mask 偵測", "Enable text_mask Detection", "text_mask 감지 사용", "text_mask 検出を有効化", "Bật phát hiện text_mask"),
        E("先显示原文", "先顯示原文", "Show Original First", "원문 먼저 표시", "先に原文を表示", "Hiện nguyên văn trước"),
        E("启用行级去重", "啟用行級去重", "Enable Line Deduplication", "줄 단위 중복 제거", "行単位の重複除去を有効化", "Bật khử trùng lặp theo dòng"),
        E("OCR 引擎选择", "OCR 引擎選擇", "OCR Engine", "OCR 엔진", "OCR エンジン", "Công cụ OCR"),
        E("PP-OCRv5 多语言版", "PP-OCRv5 多語言版", "PP-OCRv5 Multilingual", "PP-OCRv5 다국어", "PP-OCRv5 多言語版", "PP-OCRv5 đa ngôn ngữ"),
        E("OCR 识别语言 / 模型", "OCR 識別語言 / 模型", "OCR Language / Model", "OCR 인식 언어 / 모델", "OCR 認識言語 / モデル", "Ngôn ngữ / mô hình OCR"),
        E("自动 / 默认：中文+英文+日文", "自動 / 預設：中文+英文+日文", "Auto / Default: Chinese + English + Japanese", "자동 / 기본: 중국어+영어+일본어", "自動 / 既定：中国語+英語+日本語", "Tự động / mặc định: Trung + Anh + Nhật"),
        E("Chinese 中文+英文+日文", "Chinese 中文+英文+日文", "Chinese + English + Japanese", "중국어+영어+일본어", "中国語+英語+日本語", "Trung + Anh + Nhật"),
        E("English 英文", "English 英文", "English", "영어", "英語", "Tiếng Anh"),
        E("Latin 拉丁语系", "Latin 拉丁語系", "Latin scripts", "라틴 문자권", "ラテン文字圏", "Hệ chữ Latin"),
        E("Korean 韩语", "Korean 韓語", "Korean", "한국어", "韓国語", "Tiếng Hàn"),
        E("Japanese 日语", "Japanese 日語", "Japanese", "일본어", "日本語", "Tiếng Nhật"),
        E("Traditional Chinese 繁体中文", "Traditional Chinese 繁體中文", "Traditional Chinese", "번체 중국어", "繁体字中国語", "Tiếng Trung phồn thể"),
        E("Russian / East Slavic 俄语/东斯拉夫语系", "Russian / East Slavic 俄語/東斯拉夫語系", "Russian / East Slavic", "러시아어/동슬라브어", "ロシア語/東スラブ語", "Nga / Đông Slav"),
        E("Cyrillic 西里尔语系", "Cyrillic 西里爾語系", "Cyrillic scripts", "키릴 문자권", "キリル文字圏", "Hệ chữ Cyrillic"),
        E("Thai 泰语", "Thai 泰語", "Thai", "태국어", "タイ語", "Tiếng Thái"),
        E("Arabic 阿拉伯语系", "Arabic 阿拉伯語系", "Arabic scripts", "아랍 문자권", "アラビア文字圏", "Hệ chữ Ả Rập"),
        E("Devanagari 天城文", "Devanagari 天城文", "Devanagari", "데바나가리 문자", "デーヴァナーガリー", "Devanagari"),
        E("Tamil 泰米尔语", "Tamil 泰米爾語", "Tamil", "타밀어", "タミル語", "Tiếng Tamil"),
        E("Telugu 泰卢固语", "Telugu 泰盧固語", "Telugu", "텔루구어", "テルグ語", "Tiếng Telugu"),
        E("OCR 模式", "OCR 模式", "OCR Mode", "OCR 모드", "OCR モード", "Chế độ OCR"),
        E("快速模式", "快速模式", "Fast Mode", "빠른 모드", "高速モード", "Chế độ nhanh"),
        E("标准模式", "標準模式", "Standard Mode", "표준 모드", "標準モード", "Chế độ chuẩn"),
        E("高精度模式", "高精度模式", "High Accuracy Mode", "고정확도 모드", "高精度モード", "Chế độ chính xác cao"),
        E("图片放大倍率", "圖片放大倍率", "Image Scale", "이미지 확대 배율", "画像拡大率", "Tỷ lệ phóng ảnh"),
        E("原图", "原圖", "Original", "원본", "原寸", "Gốc"),
        E("对比度", "對比度", "Contrast", "대비", "コントラスト", "Tương phản"),
        E("启用轻微锐化", "啟用輕微銳化", "Enable Slight Sharpening", "약한 선명화 사용", "軽いシャープ化を有効化", "Bật làm nét nhẹ"),
        E("最低置信度", "最低信賴度", "Minimum Confidence", "최소 신뢰도", "最低信頼度", "Độ tin cậy tối thiểu"),
        E("检测/安装 PP-OCRv5 OCR 环境", "偵測/安裝 PP-OCRv5 OCR 環境", "Check / Install PP-OCRv5 OCR", "PP-OCRv5 OCR 환경 확인/설치", "PP-OCRv5 OCR環境を確認/インストール", "Kiểm tra / cài OCR PP-OCRv5"),
        E("点击按钮会检测 Python，默认在程序所在 LoLChatTranslator 文件夹创建项目专用 PP-OCRv5 OCR 虚拟环境，并安装 paddlepaddle 与 paddleocr 3.x，不会写入用户全局 Python。",
          "點擊按鈕會偵測 Python，預設在程式所在的 LoLChatTranslator 資料夾建立專案專用 PP-OCRv5 OCR 虛擬環境，並安裝 paddlepaddle 與 paddleocr 3.x，不會寫入使用者全域 Python。",
          "Click the button to check Python, create a project-only PP-OCRv5 OCR virtual environment in the LoLChatTranslator app folder by default, and install paddlepaddle plus paddleocr 3.x without touching global Python.",
          "버튼을 누르면 Python을 확인하고 기본적으로 LoLChatTranslator 프로그램 폴더에 프로젝트 전용 PP-OCRv5 OCR 가상 환경을 만든 뒤 paddlepaddle 및 paddleocr 3.x를 설치합니다. 전역 Python은 변경하지 않습니다.",
          "ボタンを押すと Python を確認し、既定では LoLChatTranslator アプリフォルダーにプロジェクト専用 PP-OCRv5 OCR 仮想環境を作成して paddlepaddle と paddleocr 3.x をインストールします。ユーザー全体の Python は変更しません。",
          "Bấm nút để kiểm tra Python, mặc định tạo môi trường OCR PP-OCRv5 riêng trong thư mục ứng dụng LoLChatTranslator và cài paddlepaddle cùng paddleocr 3.x, không ghi vào Python toàn cục."),
        E("打开频道别名文件", "開啟頻道別名檔案", "Open Channel Alias File", "채널 별칭 파일 열기", "チャンネル別名ファイルを開く", "Mở tệp bí danh kênh"),
        E("频道识别支持多语言别名。高级用户可以编辑 Resources/chat_channel_aliases.json 来添加更多语言或 OCR 误识别别名。",
          "頻道識別支援多語言別名。進階使用者可以編輯 Resources/chat_channel_aliases.json 來新增更多語言或 OCR 誤辨識別名。",
          "Channel detection supports multilingual aliases. Advanced users can edit Resources/chat_channel_aliases.json to add more languages or OCR misread aliases.",
          "채널 인식은 다국어 별칭을 지원합니다. 고급 사용자는 Resources/chat_channel_aliases.json을 편집해 언어나 OCR 오인식 별칭을 추가할 수 있습니다.",
          "チャンネル認識は多言語別名に対応しています。上級者は Resources/chat_channel_aliases.json を編集して言語や OCR 誤認識別名を追加できます。",
          "Nhận dạng kênh hỗ trợ bí danh đa ngôn ngữ. Người dùng nâng cao có thể sửa Resources/chat_channel_aliases.json để thêm ngôn ngữ hoặc bí danh OCR nhận sai."),

        E("源语言", "來源語言", "Source Language", "원본 언어", "元の言語", "Ngôn ngữ nguồn"),
        E("目标语言", "目標語言", "Target Language", "대상 언어", "翻訳先言語", "Ngôn ngữ đích"),
        E("自动检测", "自動偵測", "Auto Detect", "자동 감지", "自動検出", "Tự động nhận dạng"),
        E("翻译服务", "翻譯服務", "Translation Service", "번역 서비스", "翻訳サービス", "Dịch vụ dịch"),
        E("毒性内容显示", "毒性內容顯示", "Toxic Display", "유해 표현 표시", "有害表現表示", "Hiển thị độc hại"),
        E("安全标签", "安全標籤", "Safe Label", "안전 라벨", "安全ラベル", "Nhãn an toàn"),
        E("仅隐藏为辱骂", "僅隱藏為辱罵", "Hide as Label", "라벨로 숨김", "ラベルのみ", "Ẩn thành nhãn"),
        E("原文", "原文", "Original", "원문", "原文", "Nguyên văn"),
        E("为悬浮窗开启输入框", "為懸浮窗開啟輸入框", "Enable input box in overlay", "오버레이 입력창 사용", "オーバーレイ入力欄を有効化", "Bật ô nhập trong cửa sổ nổi"),
        E("悬浮窗输入输出语言", "懸浮窗輸入輸出語言", "Overlay Input Output Language", "오버레이 입력 출력 언어", "オーバーレイ入力の出力言語", "Ngôn ngữ đầu ra ô nhập nổi"),
        E("跟随 OCR 反向", "跟隨 OCR 反向", "Follow OCR Reverse", "OCR 반대 방향 따르기", "OCR の逆方向に追従", "Theo chiều ngược OCR"),
        E("自动失败默认语言", "自動失敗預設語言", "Auto Fallback Language", "자동 실패 시 기본 언어", "自動失敗時の既定言語", "Ngôn ngữ dự phòng tự động"),
        E("测试连接/测试翻译", "測試連線/測試翻譯", "Test Connection / Translation", "연결/번역 테스트", "接続/翻訳テスト", "Kiểm tra kết nối / dịch"),

        E("自动去掉用户名", "自動移除使用者名稱", "Remove Player Names", "플레이어 이름 자동 제거", "プレイヤー名を自動削除", "Tự bỏ tên người chơi"),
        E("去掉频道标签", "移除頻道標籤", "Remove Channel Tags", "채널 태그 제거", "チャンネルタグを削除", "Bỏ nhãn kênh"),
        E("过滤系统消息", "過濾系統訊息", "Filter System Messages", "시스템 메시지 필터", "システムメッセージを除外", "Lọc tin hệ thống"),
        E("过滤 ping 消息", "過濾 ping 訊息", "Filter Ping Messages", "핑 메시지 필터", "ping メッセージを除外", "Lọc tin ping"),
        E("过滤击杀提示", "過濾擊殺提示", "Filter Kill Announcements", "킬 알림 필터", "キル通知を除外", "Lọc thông báo hạ gục"),
        E("过滤购买提示", "過濾購買提示", "Filter Purchase Messages", "구매 알림 필터", "購入通知を除外", "Lọc thông báo mua đồ"),
        E("排除玩家", "排除玩家", "Exclude Players", "플레이어 제외", "プレイヤーを除外", "Loại trừ người chơi"),
        E("新增玩家", "新增玩家", "Add Player", "플레이어 추가", "プレイヤー追加", "Thêm người chơi"),
        E("玩家名称", "玩家名稱", "Player Name", "플레이어 이름", "プレイヤー名", "Tên người chơi"),
        E("玩家编号", "玩家編號", "Player Tag", "플레이어 태그", "プレイヤータグ", "Mã người chơi"),
        E("添加", "新增", "Add", "추가", "追加", "Thêm"),

        E("界面语言", "介面語言", "Interface Language", "인터페이스 언어", "表示言語", "Ngôn ngữ giao diện"),
        E("悬浮窗透明度", "懸浮窗透明度", "Overlay Opacity", "오버레이 투명도", "オーバーレイ不透明度", "Độ mờ cửa sổ nổi"),
        E("字体大小", "字體大小", "Font Size", "글꼴 크기", "フォントサイズ", "Cỡ chữ"),
        E("最大显示行数", "最大顯示行數", "Max Display Lines", "최대 표시 줄 수", "最大表示行数", "Số dòng tối đa"),
        E("总是置顶", "總是置頂", "Always on Top", "항상 위", "常に手前", "Luôn nổi trên cùng"),
        E("鼠标穿透", "滑鼠穿透", "Click-through", "마우스 통과", "クリック透過", "Cho chuột xuyên qua"),
        E("截图时排除悬浮窗", "截圖時排除懸浮窗", "Exclude Overlay From Capture", "캡처에서 오버레이 제외", "キャプチャからオーバーレイを除外", "Loại cửa sổ nổi khỏi ảnh chụp"),
        E("开启后会优先使用 Windows 窗口捕获排除，失败时仅在悬浮窗覆盖 OCR 区域时临时隐藏。",
          "開啟後會優先使用 Windows 視窗擷取排除，失敗時僅在懸浮窗覆蓋 OCR 區域時暫時隱藏。",
          "When enabled, Windows capture exclusion is preferred. If it fails, the overlay is hidden only while it overlaps the OCR region.",
          "켜면 Windows 캡처 제외를 우선 사용합니다. 실패하면 오버레이가 OCR 영역을 덮을 때만 잠시 숨깁니다.",
          "有効にすると Windows のキャプチャ除外を優先します。失敗した場合は、OCR 範囲に重なる時だけ一時的に隠します。",
          "Khi bật, ưu tiên loại trừ bằng Windows capture. Nếu thất bại, chỉ tạm ẩn khi cửa sổ nổi che vùng OCR."),
        E("捕获时隐藏悬浮窗", "擷取時隱藏懸浮窗", "Hide Overlay During Capture", "캡처 중 오버레이 숨기기", "キャプチャ中にオーバーレイを隠す", "Ẩn cửa sổ nổi khi chụp"),
        E("避免 OCR 识别到本程序自己的翻译结果；关闭后仅用于调试。",
          "避免 OCR 辨識到本程式自己的翻譯結果；關閉後僅用於除錯。",
          "Prevents OCR from reading this app's own translations. Disable only for debugging.",
          "OCR이 이 앱의 번역 결과를 다시 읽지 않도록 합니다. 디버깅할 때만 끄세요.",
          "OCR がこのアプリ自身の翻訳結果を読み取らないようにします。オフはデバッグ時のみ使用してください。",
          "Tránh để OCR đọc lại bản dịch của chính ứng dụng này. Chỉ tắt khi gỡ lỗi."),
        E("悬浮窗颜色（十六进制）", "懸浮窗顏色（十六進位）", "Overlay Colors (hex)", "오버레이 색상(16진수)", "オーバーレイ色（16進）", "Màu cửa sổ nổi (hex)"),
        E("悬浮窗背景", "懸浮窗背景", "Overlay Background", "오버레이 배경", "オーバーレイ背景", "Nền cửa sổ nổi"),
        E("输入框背景", "輸入框背景", "Input Background", "입력창 배경", "入力欄背景", "Nền ô nhập"),
        E("输入框文字/边框", "輸入框文字/邊框", "Input Text / Border", "입력창 글자/테두리", "入力文字/枠線", "Chữ / viền ô nhập"),
        E("文字", "文字", "Text", "텍스트", "文字", "Chữ"),
        E("边框", "邊框", "Border", "테두리", "枠線", "Viền"),
        E("频道颜色（十六进制）", "頻道顏色（十六進位）", "Channel Colors (hex)", "채널 색상(16진수)", "チャンネル色（16進）", "Màu kênh (hex)"),
        E("队伍", "隊伍", "Team", "팀", "チーム", "Đội"),
        E("正文", "正文", "Body", "본문", "本文", "Nội dung"),
        E("所有人", "所有人", "All", "전체", "全体", "Tất cả"),
        E("小队", "小隊", "Party", "파티", "パーティー", "Nhóm"),
        E("未知", "未知", "Unknown", "알 수 없음", "不明", "Không rõ"),
        E("系统", "系統", "System", "시스템", "システム", "Hệ thống"),
        E("左侧输入频道标题颜色，右侧输入译文颜色，颜色为十六进制。",
          "左側輸入頻道標題顏色，右側輸入譯文顏色，顏色為十六進位。",
          "Left field sets channel title color; right field sets translation color. Use hex colors.",
          "왼쪽은 채널 제목 색상, 오른쪽은 번역문 색상입니다. 16진수 색상을 사용하세요.",
          "左側はチャンネルタイトル色、右側は翻訳文色です。16進カラーを使ってください。",
          "Ô bên trái là màu tiêu đề kênh, bên phải là màu bản dịch. Dùng màu hex."),

        E("手动翻译一次", "手動翻譯一次", "Manual Translation Once", "수동 번역 1회", "手動翻訳 1 回", "Dịch thủ công một lần"),
        E("设置", "設定", "Set", "설정", "設定", "Đặt"),
        E("清空", "清除", "Clear", "지우기", "クリア", "Xóa"),
        E("开启/关闭自动翻译", "開啟/關閉自動翻譯", "Toggle Auto Translation", "자동 번역 켜기/끄기", "自動翻訳のオン/オフ", "Bật/tắt tự dịch"),
        E("翻译剪贴板内容", "翻譯剪貼簿內容", "Translate Clipboard", "클립보드 번역", "クリップボードを翻訳", "Dịch clipboard"),
        E("打开设置窗口", "開啟設定視窗", "Open Settings", "설정 창 열기", "設定ウィンドウを開く", "Mở cửa sổ cài đặt"),
        E("重新框选聊天区域", "重新框選聊天區域", "Select Chat Region Again", "채팅 영역 다시 선택", "チャット範囲を再選択", "Chọn lại vùng chat"),
        E("查看当前框选范围", "查看目前框選範圍", "Preview Current Region", "현재 선택 영역 미리보기", "現在の範囲を表示", "Xem vùng đã chọn"),
        E("显示/隐藏悬浮窗", "顯示/隱藏懸浮窗", "Show / Hide Overlay", "오버레이 표시/숨기기", "オーバーレイ表示/非表示", "Hiện/ẩn cửa sổ nổi"),
        E("聚焦悬浮窗输入框", "聚焦懸浮窗輸入框", "Focus Overlay Input", "오버레이 입력창 포커스", "オーバーレイ入力欄にフォーカス", "Tập trung ô nhập nổi"),
        E("项目链接", "專案連結", "Project Link", "프로젝트 링크", "プロジェクトリンク", "Liên kết dự án"),
        E("检查更新", "檢查更新", "Check Updates", "업데이트 확인", "更新を確認", "Kiểm tra cập nhật"),
        E("删除 PP-OCRv5 OCR 环境", "刪除 PP-OCRv5 OCR 環境", "Delete PP-OCRv5 OCR Environment", "PP-OCRv5 OCR 환경 삭제", "PP-OCRv5 OCR環境を削除", "Xóa môi trường OCR PP-OCRv5"),

        E("选择颜色", "選擇顏色", "Choose Color", "색상 선택", "色を選択", "Chọn màu"),
        E("颜色预览", "顏色預覽", "Color Preview", "색상 미리보기", "色プレビュー", "Xem trước màu"),
        E("打开色盘", "開啟色盤", "Open color picker", "색상 선택 열기", "カラーピッカーを開く", "Mở bảng màu"),
        E("设置快捷键", "設定快捷鍵", "Set Hotkey", "단축키 설정", "ホットキー設定", "Đặt phím tắt"),
        E("请直接按下新的快捷键", "請直接按下新的快捷鍵", "Press the new hotkey", "새 단축키를 누르세요", "新しいホットキーを押してください", "Nhấn phím tắt mới"),
        E("支持 F1-F24，或 Ctrl / Alt / Shift 组合键。按 Esc 取消。",
          "支援 F1-F24，或 Ctrl / Alt / Shift 組合鍵。按 Esc 取消。",
          "Supports F1-F24 or Ctrl / Alt / Shift combinations. Press Esc to cancel.",
          "F1-F24 또는 Ctrl / Alt / Shift 조합을 지원합니다. Esc로 취소합니다.",
          "F1-F24、または Ctrl / Alt / Shift の組み合わせに対応。Esc でキャンセル。",
          "Hỗ trợ F1-F24 hoặc tổ hợp Ctrl / Alt / Shift. Nhấn Esc để hủy."),
        E("当前框选范围", "目前框選範圍", "Current Region", "현재 선택 영역", "現在の範囲", "Vùng hiện tại"),
        E("绿色区域是当前 OCR 聊天框选范围。点击任意位置或按 Esc 关闭。",
          "綠色區域是目前 OCR 聊天框選範圍。點擊任意位置或按 Esc 關閉。",
          "The green area is the current OCR chat region. Click anywhere or press Esc to close.",
          "초록색 영역이 현재 OCR 채팅 선택 범위입니다. 아무 곳이나 클릭하거나 Esc로 닫으세요.",
          "緑の範囲が現在の OCR チャット範囲です。任意の場所をクリック、または Esc で閉じます。",
          "Vùng xanh là vùng chat OCR hiện tại. Bấm bất kỳ đâu hoặc nhấn Esc để đóng."),
        E("选择聊天区域", "選擇聊天區域", "Select Chat Region", "채팅 영역 선택", "チャット範囲を選択", "Chọn vùng chat"),
        E("拖拽框选游戏聊天区域，松开鼠标确认；按 Esc 取消",
          "拖曳框選遊戲聊天區域，放開滑鼠確認；按 Esc 取消",
          "Drag to select the in-game chat region; release to confirm. Press Esc to cancel.",
          "드래그해 게임 채팅 영역을 선택하고 놓으면 확인됩니다. Esc로 취소합니다.",
          "ドラッグしてゲーム内チャット範囲を選択し、離すと確定します。Esc でキャンセル。",
          "Kéo để chọn vùng chat trong game; thả chuột để xác nhận. Nhấn Esc để hủy."),
        E("发现新版本", "發現新版本", "Update Available", "새 버전 발견", "新しいバージョンがあります", "Có phiên bản mới"),
        E("OCR 测试结果", "OCR 測試結果", "OCR Test Results", "OCR 테스트 결과", "OCR テスト結果", "Kết quả kiểm tra OCR"),
        E("屏幕截取", "螢幕截取", "Screen Capture", "화면 캡처", "画面キャプチャ", "Ảnh chụp màn hình"),
        E("屏幕截图", "螢幕截圖", "Screenshot", "스크린샷", "スクリーンショット", "Ảnh chụp"),
        E("处理后图片", "處理後圖片", "Processed Image", "처리 후 이미지", "処理後画像", "Ảnh đã xử lý"),
        E("原图 OCR", "原圖 OCR", "Original OCR", "원본 OCR", "原寸 OCR", "OCR ảnh gốc"),
        E("灰度 OCR", "灰階 OCR", "Grayscale OCR", "그레이스케일 OCR", "グレースケール OCR", "OCR ảnh xám"),
        E("对比度增强 OCR", "對比度增強 OCR", "Contrast OCR", "대비 강화 OCR", "コントラスト強調 OCR", "OCR tăng tương phản"),
        E("二值化 OCR", "二值化 OCR", "Binary OCR", "이진화 OCR", "二値化 OCR", "OCR nhị phân"),
        E("<no text>", "<無文字>", "<no text>", "<텍스트 없음>", "<テキストなし>", "<không có văn bản>")
    ];

    private static readonly Dictionary<string, Entry> ReverseLookup = BuildReverseLookup();

    public static string Text(
        string language,
        string zhHans,
        string zhHant,
        string en,
        string ko,
        string ja,
        string vi)
    {
        return new Entry(zhHans, zhHant, en, ko, ja, vi).For(language);
    }

    public static string Localize(string language, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return ReverseLookup.TryGetValue(value.Trim(), out var entry)
            ? entry.For(language)
            : value;
    }

    public static void ApplyTo(DependencyObject root, string language)
    {
        ApplyTo(root, LocalizationService.NormalizeLanguage(language), []);
    }

    private static Entry E(string zhHans, string zhHant, string en, string ko, string ja, string vi)
    {
        return new Entry(zhHans, zhHant, en, ko, ja, vi);
    }

    private static Dictionary<string, Entry> BuildReverseLookup()
    {
        var lookup = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var entry in Entries)
        {
            foreach (var value in entry.Values())
            {
                lookup.TryAdd(value.Trim(), entry);
            }
        }

        return lookup;
    }

    private static void ApplyTo(DependencyObject root, string language, HashSet<DependencyObject> visited)
    {
        if (!visited.Add(root))
        {
            return;
        }

        LocalizeObject(root, language);

        foreach (var child in GetChildren(root))
        {
            ApplyTo(child, language, visited);
        }
    }

    private static void LocalizeObject(DependencyObject target, string language)
    {
        if (target is TextBlock textBlock
            && BindingOperations.GetBindingBase(textBlock, TextBlock.TextProperty) is null
            && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            textBlock.Text = Localize(language, textBlock.Text);
        }

        if (target is ContentControl contentControl
            && BindingOperations.GetBindingBase(contentControl, ContentControl.ContentProperty) is null
            && contentControl.Content is string content)
        {
            contentControl.Content = Localize(language, content);
        }

        if (target is HeaderedContentControl headeredControl
            && BindingOperations.GetBindingBase(headeredControl, HeaderedContentControl.HeaderProperty) is null
            && headeredControl.Header is string header)
        {
            headeredControl.Header = Localize(language, header);
        }

        if (target is FrameworkElement { ToolTip: string toolTip } element)
        {
            element.ToolTip = Localize(language, toolTip);
        }
    }

    private static IEnumerable<DependencyObject> GetChildren(DependencyObject root)
    {
        var visualCount = 0;
        try
        {
            visualCount = VisualTreeHelper.GetChildrenCount(root);
        }
        catch
        {
            // Some logical-only objects are not in the visual tree.
        }

        for (var i = 0; i < visualCount; i++)
        {
            yield return VisualTreeHelper.GetChild(root, i);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
        }
    }
}
